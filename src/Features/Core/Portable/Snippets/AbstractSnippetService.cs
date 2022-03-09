﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.PooledObjects;

namespace Microsoft.CodeAnalysis.Snippets
{
    internal abstract class AbstractSnippetService : ISnippetService
    {
        private readonly ImmutableArray<Lazy<ISnippetProvider, LanguageMetadata>> _lazySnippetProviders;
        private readonly Dictionary<string, ISnippetProvider> _identifierToProviderMap = new();
        private ImmutableArray<ISnippetProvider> _snippetProviders;

        public AbstractSnippetService(IEnumerable<Lazy<ISnippetProvider, LanguageMetadata>> lazySnippetProviders)
        {
            _lazySnippetProviders = lazySnippetProviders.ToImmutableArray();
        }

        /// <summary>
        /// This should never be called prior to GetSnippetsAsync because it gets populated
        /// at that point in time.
        /// </summary>
        public ISnippetProvider GetSnippetProvider(string snippetIdentifier)
        {
            return _identifierToProviderMap[snippetIdentifier];
        }

        /// <summary>
        /// Iterates through all providers and determines if the snippet 
        /// can be added to the Completion list at the corresponding position.
        /// </summary>
        public async Task<ImmutableArray<SnippetData>> GetSnippetsAsync(Document document, int position, CancellationToken cancellationToken)
        {
            using var _ = ArrayBuilder<SnippetData>.GetInstance(out var arrayBuilder);
            foreach (var provider in GetSnippetProviders(document))
            {
                var snippetData = await provider.GetSnippetDataAsync(document, position, cancellationToken).ConfigureAwait(false);
                arrayBuilder.AddIfNotNull(snippetData);
            }

            return arrayBuilder.ToImmutable();
        }

        private ImmutableArray<ISnippetProvider> GetSnippetProviders(Document document)
        {
            if (_snippetProviders.IsDefault)
            {
                using var _ = ArrayBuilder<ISnippetProvider>.GetInstance(out var arrayBuilder);
                foreach (var provider in _lazySnippetProviders.Where(p => p.Metadata.Language == document.Project.Language))
                {
                    var providerData = provider.Value;
                    arrayBuilder.Add(providerData);
                    _identifierToProviderMap.Add(providerData.SnippetIdentifier, providerData);
                }

                _snippetProviders = arrayBuilder.ToImmutable();
            }

            return _snippetProviders;
        }
    }
}
