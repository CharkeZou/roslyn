﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.FindSymbols.Finders;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.FindSymbols;

using Reference = (SymbolGroup group, ISymbol symbol, ReferenceLocation location);

internal partial class FindReferencesSearchEngine
{
    private static readonly ObjectPool<MetadataUnifyingSymbolHashSet> s_metadataUnifyingSymbolHashSetPool = new(() => []);
    private static readonly UnboundedChannelOptions s_channelOptions = new() { SingleReader = true };

    private readonly Solution _solution;
    private readonly IImmutableSet<Document>? _documents;
    private readonly ImmutableArray<IReferenceFinder> _finders;
    private readonly IStreamingProgressTracker _progressTracker;
    private readonly IStreamingFindReferencesProgress _progress;
    private readonly FindReferencesSearchOptions _options;

    /// <summary>
    /// Scheduler to run our tasks on.  If we're in <see cref="FindReferencesSearchOptions.Explicit"/> mode, we'll
    /// run all our tasks concurrently.  Otherwise, we will run them serially using <see cref="s_exclusiveScheduler"/>
    /// </summary>
    private readonly TaskScheduler _scheduler;
    private static readonly TaskScheduler s_exclusiveScheduler = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;

    /// <summary>
    /// Mapping from symbols (unified across metadata/retargeting) and the set of symbols that was produced for 
    /// them in the case of linked files across projects.  This allows references to be found to any of the unified
    /// symbols, while the user only gets a single reported group back that corresponds to that entire set.
    /// </summary>
    private readonly ConcurrentDictionary<ISymbol, SymbolGroup> _symbolToGroup = new(MetadataUnifyingEquivalenceComparer.Instance);

    public FindReferencesSearchEngine(
        Solution solution,
        IImmutableSet<Document>? documents,
        ImmutableArray<IReferenceFinder> finders,
        IStreamingFindReferencesProgress progress,
        FindReferencesSearchOptions options)
    {
        _documents = documents;
        _solution = solution;
        _finders = finders;
        _progress = progress;
        _options = options;

        _progressTracker = progress.ProgressTracker;

        // If we're an explicit invocation, just defer to the threadpool to execute all our work in parallel to get
        // things done as quickly as possible.  If we're running implicitly, then use a
        // ConcurrentExclusiveSchedulerPair's exclusive scheduler as that's the most built-in way in the TPL to get
        // will run things serially.
        _scheduler = _options.Explicit ? TaskScheduler.Default : s_exclusiveScheduler;
    }

    public Task FindReferencesAsync(ISymbol symbol, CancellationToken cancellationToken)
        => FindReferencesAsync([symbol], cancellationToken);

    public async Task FindReferencesAsync(
        ImmutableArray<ISymbol> symbols, CancellationToken cancellationToken)
    {
        await _progress.OnStartedAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ProducerConsumer<Reference>.RunUnboundedAsync(
                s_channelOptions,
                produceItems: static (onItemFound, args) => args.@this.PerformSearchAsync(args.symbols, onItemFound, args.cancellationToken),
                consumeItems: static async (references, args) => await args.@this._progress.OnReferencesFoundAsync(references, @args.cancellationToken).ConfigureAwait(false),
                (@this: this, symbols, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _progress.OnCompletedAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PerformSearchAsync(
        ImmutableArray<ISymbol> symbols, Action<Reference> onReferenceFound, CancellationToken cancellationToken)
    {
        var unifiedSymbols = new MetadataUnifyingSymbolHashSet();
        unifiedSymbols.AddRange(symbols);

        var disposable = await _progressTracker.AddSingleItemAsync(cancellationToken).ConfigureAwait(false);
        await using var _ = disposable.ConfigureAwait(false);

        // Create the initial set of symbols to search for.  As we walk the appropriate projects in the solution
        // we'll expand this set as we discover new symbols to search for in each project.
        var symbolSet = await SymbolSet.CreateAsync(
            this, unifiedSymbols, includeImplementationsThroughDerivedTypes: true, cancellationToken).ConfigureAwait(false);

        // Report the initial set of symbols to the caller.
        var allSymbols = symbolSet.GetAllSymbols();
        await ReportGroupsAsync(allSymbols, cancellationToken).ConfigureAwait(false);

        // Determine the set of projects we actually have to walk to find results in.  If the caller provided a
        // set of documents to search, we only bother with those.
        var projectsToSearch = await GetProjectsToSearchAsync(allSymbols, cancellationToken).ConfigureAwait(false);

        // We need to process projects in order when updating our symbol set.  Say we have three projects (A, B
        // and C), we cannot necessarily find inherited symbols in C until we have searched B.  Importantly,
        // while we're processing each project linearly to update the symbol set we're searching for, we still
        // then process the projects in parallel once we know the set of symbols we're searching for in that
        // project.
        await _progressTracker.AddItemsAsync(projectsToSearch.Length, cancellationToken).ConfigureAwait(false);

        // Pull off and start searching each project as soon as we can once we've done the inheritance cascade into it.
        await RoslynParallel.ForEachAsync(
            GetProjectsAndSymbolsToSearchAsync(symbolSet, projectsToSearch, cancellationToken),
            cancellationToken,
            async (tuple, cancellationToken) => await ProcessProjectAsync(
                tuple.project, tuple.allSymbols, onReferenceFound, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<(Project project, ImmutableArray<ISymbol> allSymbols)> GetProjectsAndSymbolsToSearchAsync(
        SymbolSet symbolSet,
        ImmutableArray<Project> projectsToSearch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var dependencyGraph = _solution.GetProjectDependencyGraph();
        foreach (var projectId in dependencyGraph.GetTopologicallySortedProjects(cancellationToken))
        {
            var currentProject = _solution.GetRequiredProject(projectId);
            if (!projectsToSearch.Contains(currentProject))
                continue;

            // As we walk each project, attempt to grow the search set appropriately up and down the inheritance
            // hierarchy and grab a copy of the symbols to be processed.  Note: this has to happen serially
            // which is why we do it in this loop and not inside the concurrent project processing that happens
            // below.
            await symbolSet.InheritanceCascadeAsync(currentProject, cancellationToken).ConfigureAwait(false);
            var allSymbols = symbolSet.GetAllSymbols();

            // Report any new symbols we've cascaded to to our caller.
            await ReportGroupsAsync(allSymbols, cancellationToken).ConfigureAwait(false);

            yield return (currentProject, allSymbols);
        }
    }

    public Task CreateWorkAsync(Func<Task> createWorkAsync, CancellationToken cancellationToken)
        => Task.Factory.StartNew(createWorkAsync, cancellationToken, TaskCreationOptions.None, _scheduler).Unwrap();

    /// <summary>
    /// Notify the caller of the engine about the definitions we've found that we're looking for.  We'll only notify
    /// them once per symbol group, but we may have to notify about new symbols each time we expand our symbol set
    /// when we walk into a new project.
    /// </summary>
    private async Task ReportGroupsAsync(ImmutableArray<ISymbol> symbols, CancellationToken cancellationToken)
    {
        foreach (var symbol in symbols)
            await ReportGroupAsync(symbol, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SymbolGroup> ReportGroupAsync(ISymbol symbol, CancellationToken cancellationToken)
    {
        // See if this is the first time we're running across this symbol.  Note: no locks are needed
        // here between checking and then adding because this is only ever called serially from within
        // FindReferencesAsync above (though we still need a ConcurrentDictionary as reads of these 
        // symbols will happen later in ProcessDocumentAsync.  However, those reads will only happen
        // after the dependent symbol values were written in, so it will be safe to blindly read them
        // out.
        if (!_symbolToGroup.TryGetValue(symbol, out var group))
        {
            var linkedSymbols = await SymbolFinder.FindLinkedSymbolsAsync(symbol, _solution, cancellationToken).ConfigureAwait(false);
            Contract.ThrowIfFalse(linkedSymbols.Contains(symbol), "Linked symbols did not contain the very symbol we started with.");

            group = new SymbolGroup(linkedSymbols);
            Contract.ThrowIfFalse(group.Symbols.Contains(symbol), "Symbol group did not contain the very symbol we started with.");

            foreach (var groupSymbol in group.Symbols)
                _symbolToGroup.TryAdd(groupSymbol, group);

            // Since "symbol" was in group.Symbols, and we just added links from all of group.Symbols to that group, then "symbol" 
            // better now be in _symbolToGroup.
            Contract.ThrowIfFalse(_symbolToGroup.ContainsKey(symbol));

            await _progress.OnDefinitionFoundAsync(group, cancellationToken).ConfigureAwait(false);
        }

        return group;
    }

    private Task<ImmutableArray<Project>> GetProjectsToSearchAsync(
        ImmutableArray<ISymbol> symbols, CancellationToken cancellationToken)
    {
        var projects = _documents != null
            ? _documents.Select(d => d.Project).ToImmutableHashSet()
            : _solution.Projects.ToImmutableHashSet();

        return DependentProjectsFinder.GetDependentProjectsAsync(_solution, symbols, projects, cancellationToken);
    }

    private async ValueTask ProcessProjectAsync(
        Project project, ImmutableArray<ISymbol> allSymbols, Action<Reference> onReferenceFound, CancellationToken cancellationToken)
    {
        using var _1 = PooledDictionary<ISymbol, PooledHashSet<string>>.GetInstance(out var symbolToGlobalAliases);
        using var _2 = PooledDictionary<Document, MetadataUnifyingSymbolHashSet>.GetInstance(out var documentToSymbols);
        try
        {
            // scratch hashset to place results in. Populated/inspected/cleared in inner loop.
            using var _3 = PooledHashSet<Document>.GetInstance(out var foundDocuments);

            await AddGlobalAliasesAsync(project, allSymbols, symbolToGlobalAliases, cancellationToken).ConfigureAwait(false);

            foreach (var symbol in allSymbols)
            {
                var globalAliases = TryGet(symbolToGlobalAliases, symbol);

                foreach (var finder in _finders)
                {
                    await finder.DetermineDocumentsToSearchAsync(
                        symbol, globalAliases, project, _documents,
                        StandardCallbacks<Document>.AddToHashSet,
                        foundDocuments,
                        _options, cancellationToken).ConfigureAwait(false);

                    foreach (var document in foundDocuments)
                    {
                        var docSymbols = GetSymbolSet(documentToSymbols, document);
                        docSymbols.Add(symbol);
                    }

                    foundDocuments.Clear();
                }
            }

            await RoslynParallel.ForEachAsync(
                documentToSymbols,
                cancellationToken,
                (kvp, cancellationToken) =>
                    ProcessDocumentAsync(kvp.Key, kvp.Value, symbolToGlobalAliases, onReferenceFound, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            foreach (var (_, symbols) in documentToSymbols)
            {
                symbols.Clear();
                s_metadataUnifyingSymbolHashSetPool.Free(symbols);
            }

            FreeGlobalAliases(symbolToGlobalAliases);

            await _progressTracker.ItemCompletedAsync(cancellationToken).ConfigureAwait(false);
        }

        static MetadataUnifyingSymbolHashSet GetSymbolSet<T>(PooledDictionary<T, MetadataUnifyingSymbolHashSet> dictionary, T key) where T : notnull
        {
            if (!dictionary.TryGetValue(key, out var set))
            {
                set = s_metadataUnifyingSymbolHashSetPool.Allocate();
                dictionary.Add(key, set);
            }

            return set;
        }
    }

    private static PooledHashSet<U>? TryGet<T, U>(Dictionary<T, PooledHashSet<U>> dictionary, T key) where T : notnull
        => dictionary.TryGetValue(key, out var set) ? set : null;

    private async ValueTask ProcessDocumentAsync(
        Document document,
        MetadataUnifyingSymbolHashSet symbols,
        Dictionary<ISymbol, PooledHashSet<string>> symbolToGlobalAliases,
        Action<Reference> onReferenceFound,
        CancellationToken cancellationToken)
    {
        // We're doing to do all of our processing of this document at once.  This will necessitate all the
        // appropriate finders checking this document for hits.  We know that in the initial pass to determine
        // documents, this document was already considered a strong match (e.g. we know it contains the name of
        // the symbol being searched for).  As such, we're almost certainly going to have to do semantic checks
        // to now see if the candidate actually matches the symbol.  This will require syntax and semantics.  So
        // just grab those once here and hold onto them for the lifetime of this call.
        var cache = await FindReferenceCache.GetCacheAsync(document, cancellationToken).ConfigureAwait(false);

        // This search almost always involves trying to find the tokens matching the name of the symbol we're looking
        // for.  Get the cache ready with those tokens so that kicking of N searches to search for each symbol in
        // parallel doesn't cause us to compute and cache the same thing concurrently.

        // Note: cascaded symbols will normally have the same name.  That's ok.  The second call to
        // FindMatchingIdentifierTokens with the same name will short circuit since it will already see the result of
        // the prior call.
        foreach (var symbol in symbols)
        {
            if (symbol.CanBeReferencedByName)
                cache.FindMatchingIdentifierTokens(symbol.Name, cancellationToken);
        }

        await RoslynParallel.ForEachAsync(
            symbols,
            cancellationToken,
            async (symbol, cancellationToken) =>
            {
                // symbolToGlobalAliases is safe to read in parallel.  It is created fully before this point and is no
                // longer mutated.
                var globalAliases = TryGet(symbolToGlobalAliases, symbol);
                var state = new FindReferencesDocumentState(cache, globalAliases);

                await ProcessDocumentAsync(symbol, state, onReferenceFound).ConfigureAwait(false);
            }).ConfigureAwait(false);

        return;

        async Task ProcessDocumentAsync(
            ISymbol symbol, FindReferencesDocumentState state, Action<Reference> onReferenceFound)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (Logger.LogBlock(FunctionId.FindReference_ProcessDocumentAsync, cancellationToken))
            {
                // This is safe to just blindly read. We can only ever get here after the call to ReportGroupsAsync
                // happened.  So there must be a group for this symbol in our map.
                var group = _symbolToGroup[symbol];

                // Note: nearly every finder will no-op when passed a in a symbol it's not applicable to.  So it's
                // simple to just iterate over all of them, knowing that will quickly skip all the irrelevant ones,
                // and only do interesting work on the single relevant one.
                foreach (var finder in _finders)
                {
                    await finder.FindReferencesInDocumentAsync(
                        symbol, state,
                        static (loc, tuple) => tuple.onReferenceFound((tuple.group, tuple.symbol, loc.Location)),
                        (group, symbol, onReferenceFound),
                        _options,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task AddGlobalAliasesAsync(
        Project project,
        ImmutableArray<ISymbol> allSymbols,
        PooledDictionary<ISymbol, PooledHashSet<string>> symbolToGlobalAliases,
        CancellationToken cancellationToken)
    {
        foreach (var symbol in allSymbols)
        {
            foreach (var finder in _finders)
            {
                var aliases = await finder.DetermineGlobalAliasesAsync(
                    symbol, project, cancellationToken).ConfigureAwait(false);
                if (aliases.Length > 0)
                {
                    var globalAliases = GetGlobalAliasesSet(symbolToGlobalAliases, symbol);
                    globalAliases.AddRange(aliases);
                }
            }
        }
    }

    private static PooledHashSet<string> GetGlobalAliasesSet<T>(PooledDictionary<T, PooledHashSet<string>> dictionary, T key) where T : notnull
    {
        if (!dictionary.TryGetValue(key, out var set))
        {
            set = PooledHashSet<string>.GetInstance();
            dictionary.Add(key, set);
        }

        return set;
    }

    private static void FreeGlobalAliases(PooledDictionary<ISymbol, PooledHashSet<string>> symbolToGlobalAliases)
    {
        foreach (var (_, globalAliases) in symbolToGlobalAliases)
            globalAliases.Free();
    }

    private static bool InvolvesInheritance(ISymbol symbol)
        => symbol is IMethodSymbol or IPropertySymbol or IEventSymbol;
}
