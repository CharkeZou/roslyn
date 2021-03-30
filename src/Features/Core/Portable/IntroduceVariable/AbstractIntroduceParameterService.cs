﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeGeneration;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Simplification;
using Roslyn.Utilities;
using static Microsoft.CodeAnalysis.CodeActions.CodeAction;

namespace Microsoft.CodeAnalysis.IntroduceVariable
{
    internal abstract partial class AbstractIntroduceParameterService<
        TExpressionSyntax,
        TInvocationExpressionSyntax,
        TIdentifierNameSyntax> : CodeRefactoringProvider
        where TExpressionSyntax : SyntaxNode
        where TInvocationExpressionSyntax : TExpressionSyntax
        where TIdentifierNameSyntax : TExpressionSyntax
    {
        protected abstract bool IsContainedInParameterizedDeclaration(SyntaxNode node);
        protected abstract SyntaxNode GenerateExpressionFromOptionalParameter(IParameterSymbol parameterSymbol);

        public sealed override async Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var (document, textSpan, cancellationToken) = context;
            if (document.Project.Solution.Workspace.Kind == WorkspaceKind.MiscellaneousFiles)
            {
                return;
            }

            var expression = await document.TryGetRelevantNodeAsync<TExpressionSyntax>(textSpan, cancellationToken).ConfigureAwait(false);
            if (expression == null || CodeRefactoringHelpers.IsNodeUnderselected(expression, textSpan))
            {
                return;
            }

            var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            var expressionType = semanticModel.GetTypeInfo(expression, cancellationToken).Type;
            if (expressionType is null or IErrorTypeSymbol)
            {
                return;
            }

            var syntaxFacts = document.GetRequiredLanguageService<ISyntaxFactsService>();
            var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var generator = SyntaxGenerator.GetGenerator(document);
            var containingMethod = expression.FirstAncestorOrSelf<SyntaxNode>(node => generator.GetParameterListNode(node) is not null);

            if (containingMethod is null)
            {
                return;
            }

            var containingSymbol = semanticModel.GetDeclaredSymbol(containingMethod, cancellationToken);
            if (containingSymbol is not IMethodSymbol methodSymbol)
            {
                return;
            }

            var methodKind = methodSymbol.MethodKind;
            if (methodKind is not MethodKind.Ordinary && methodKind is not MethodKind.Constructor &&
                methodKind is not MethodKind.LambdaMethod && methodKind is not MethodKind.LocalFunction)
            {
                return;
            }

            var actions = await AddActionsAsync(document, expression, methodSymbol, containingMethod, cancellationToken).ConfigureAwait(false);

            if (actions.Length == 0)
            {
                return;
            }

            var action = new CodeActionWithNestedActions(FeaturesResources.Introduce_parameter, actions, isInlinable: false);
            context.RegisterRefactoring(action, textSpan);
        }

        /// <summary>
        /// Introduces a new parameter and refactors all the call sites.
        /// </summary>
        public async Task<Solution> IntroduceParameterAsync(Document document, TExpressionSyntax expression,
            IMethodSymbol methodSymbol, SyntaxNode containingMethod, bool allOccurrences, bool trampoline, bool overload,
            CancellationToken cancellationToken)
        {
            var parameterName = await GetNewParameterNameAsync(document, expression, cancellationToken).ConfigureAwait(false);

            var methodCallSites = await FindCallSitesAsync(document, methodSymbol, cancellationToken).ConfigureAwait(false);

            return await RewriteSolutionAsync(document,
                expression, methodSymbol, containingMethod, allOccurrences, parameterName, methodCallSites,
                trampoline, overload, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Gets the parameter name, if the expression's grandparent is a variable declarator then it just gets the
        /// local declarations name. Otherwise, it generates a name based on the context of the expression.
        /// </summary>
        private static async Task<string> GetNewParameterNameAsync(Document document, TExpressionSyntax expression, CancellationToken cancellationToken)
        {
            if (ShouldRemoveVariableDeclaratorContainingExpression(document, expression, out var varDeclName, out _))
            {
                return varDeclName;
            }

            var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var semanticFacts = document.GetRequiredLanguageService<ISemanticFactsService>();
            return semanticFacts.GenerateNameForExpression(semanticModel, expression, capitalize: false, cancellationToken);
        }

        /// <summary>
        /// Determines if the expression's grandparent is a variable declarator and if so,
        /// returns the name
        /// </summary>
        protected static bool ShouldRemoveVariableDeclaratorContainingExpression(
            Document document, TExpressionSyntax expression, [NotNullWhen(true)] out string? varDeclName, [NotNullWhen(true)] out SyntaxNode? localDeclaration)
        {
            var syntaxFacts = document.GetRequiredLanguageService<ISyntaxFactsService>();
            localDeclaration = expression.FirstAncestorOrSelf<SyntaxNode>(node => syntaxFacts.IsLocalDeclarationStatement(node));
            if (localDeclaration is null)
            {
                varDeclName = null;
                return false;
            }

            if (syntaxFacts.GetVariablesOfLocalDeclarationStatement(localDeclaration).Count > 1)
            {
                varDeclName = null;
                localDeclaration = null;
                return false;
            }

            var expressionDecl = expression?.Parent?.Parent;
            if (syntaxFacts.IsVariableDeclarator(expressionDecl))
            {
                varDeclName = syntaxFacts.GetIdentifierOfVariableDeclarator(expressionDecl).ValueText;
                return true;
            }

            varDeclName = null;
            localDeclaration = null;
            return false;
        }

        /// <summary>
        /// Locates all the call sites of the method that introduced the parameter
        /// </summary>
        protected static async Task<ImmutableDictionary<Document, List<TInvocationExpressionSyntax>>> FindCallSitesAsync(
            Document document, IMethodSymbol methodSymbol, CancellationToken cancellationToken)
        {
            var methodCallSitesBuilder = ImmutableDictionary.CreateBuilder<Document, List<TInvocationExpressionSyntax>>();
            var progress = new StreamingProgressCollector();
            await SymbolFinder.FindReferencesAsync(
                methodSymbol, document.Project.Solution, progress,
                documents: null, FindReferencesSearchOptions.Default, cancellationToken).ConfigureAwait(false);
            var referencedSymbols = progress.GetReferencedSymbols();

            // Ordering by descending to sort invocations by starting span to account for nested invocations
            var referencedLocations = referencedSymbols.SelectMany(referencedSymbol => referencedSymbol.Locations).Distinct()
                .OrderByDescending(reference => reference.Location.SourceSpan.Start);

            // Adding the original document to account for refactorings that do not have any invocations
            methodCallSitesBuilder.Add(document, new List<TInvocationExpressionSyntax>());

            foreach (var refLocation in referencedLocations)
            {
                // Does not support cross-language references currently
                if (refLocation.Document.Project.Language == document.Project.Language)
                {
                    var reference = refLocation.Location.FindNode(cancellationToken).GetRequiredParent();

                    // Only adding items that are of type InvocationExpressionSyntax
                    if (reference is not TInvocationExpressionSyntax invocation)
                    {
                        continue;
                    }

                    if (!methodCallSitesBuilder.TryGetValue(refLocation.Document, out var list))
                    {
                        list = new List<TInvocationExpressionSyntax>();
                        methodCallSitesBuilder.Add(refLocation.Document, list);
                    }

                    list.Add(invocation);
                }
            }

            return methodCallSitesBuilder.ToImmutable();
        }

        /// <summary>
        /// If the parameter is optional and the invocation does not specify the parameter, then
        /// a named argument needs to be introduced.
        /// </summary>
        private static SeparatedSyntaxList<SyntaxNode> AddArgumentToArgumentList(
            SeparatedSyntaxList<SyntaxNode> invocationArguments, SyntaxGenerator generator,
            SyntaxNode newArgumentExpression, int insertionIndex, string name, bool named)
        {
            var argument = named
                ? generator.Argument(name, RefKind.None,
                    newArgumentExpression)
                :
                generator.Argument(newArgumentExpression);

            return invocationArguments.Insert(insertionIndex, argument.WithAdditionalAnnotations(Simplifier.Annotation));
        }

        /// <summary>
        /// Gets the matches of the expression and replaces them with the identifier.
        /// Special case for the original matching expression, if its parent is a LocalDeclarationStatement then it can
        /// be removed because assigning the local dec variable to a parameter is repetitive. Does not need a rename
        /// annotation since the user has already named the local declaration.
        /// Otherwise, it needs to have a rename annotation added to it because the new parameter gets a randomly
        /// generated name that the user can immediately change.
        /// </summary>
        public static async Task UpdateExpressionInOriginalFunctionAsync(Document document,
            TExpressionSyntax expression, SyntaxNode scope, string parameterName, SyntaxEditor editor,
            bool allOccurrences, CancellationToken cancellationToken)
        {
            var generator = editor.Generator;
            var matches = await FindMatchesAsync(document, expression, scope, allOccurrences, cancellationToken).ConfigureAwait(false);
            var replacement = (TIdentifierNameSyntax)generator.IdentifierName(parameterName);

            foreach (var match in matches)
            {
                // Special case the removal of the originating expression to either remove the local declaration
                // or to add a rename annotation.
                if (!match.Equals(expression))
                {
                    editor.ReplaceNode(match, replacement);
                }
                else
                {
                    if (ShouldRemoveVariableDeclaratorContainingExpression(document, expression, out var varDeclName, out var localDeclaration))
                    {
                        editor.RemoveNode(localDeclaration);
                    }
                    else
                    {
                        // Creating a SyntaxToken from the new parameter name and adding an annotation to it
                        // and passing that syntaxtoken in to generator.IdentifierName to create a new
                        // IdentifierNameSyntax node.
                        // Need to create the RenameAnnotation on the token itself otherwise it does not show up
                        // properly.
                        replacement = (TIdentifierNameSyntax)generator.IdentifierName(generator.Identifier(parameterName)
                            .WithAdditionalAnnotations(RenameAnnotation.Create()));
                        editor.ReplaceNode(match, replacement);
                    }
                }
            }
        }

        /// <summary>
        /// Goes through the parameters of the original method to get the location that the parameter
        /// and argument should be introduced.
        /// </summary>
        private static int GetInsertionIndex(Compilation compilation, IMethodSymbol methodSymbol,
            ISyntaxFactsService syntaxFacts, SyntaxNode methodDeclaration)
        {
            var parameterList = syntaxFacts.GetParameterList(methodDeclaration);
            Contract.ThrowIfNull(parameterList);
            var insertionIndex = 0;

            foreach (var parameterSymbol in methodSymbol.Parameters)
            {
                // Want to skip optional parameters, params parameters, and CancellationToken since they should be at
                // the end of the list.
                if (!parameterSymbol.HasExplicitDefaultValue && !parameterSymbol.IsParams &&
                        !parameterSymbol.Type.Equals(compilation.GetTypeByMetadataName(typeof(CancellationToken)?.FullName!)))
                {
                    insertionIndex++;
                }
            }

            return insertionIndex;
        }

        /// <summary>
        /// Goes through all of the invocations and replaces the expression with identifiers from the invocation 
        /// arguments and rewrites the call site with the updated expression as a new argument.
        /// </summary>
        private async Task<Solution> RewriteSolutionAsync(
            Document originalDocument, TExpressionSyntax expression, IMethodSymbol methodSymbol,
            SyntaxNode containingMethod, bool allOccurrences, string parameterName,
            ImmutableDictionary<Document, List<TInvocationExpressionSyntax>> callSites, bool trampoline,
            bool overload, CancellationToken cancellationToken)
        {
            var modifiedSolution = originalDocument.Project.Solution;
            var mappingDictionary = await MapExpressionToParametersAsync(originalDocument, expression, cancellationToken).ConfigureAwait(false);
            var variablesInExpression = expression.DescendantNodes().OfType<TIdentifierNameSyntax>();

            foreach (var (project, projectCallSites) in callSites.GroupBy(kvp => kvp.Key.Project))
            {
                var compilation = await project.GetRequiredCompilationAsync(cancellationToken).ConfigureAwait(false);
                foreach (var (document, invocationExpressionList) in projectCallSites)
                {
                    if (trampoline || overload)
                    {
                        var newRoot = await ModifyDocumentInvocationsTrampolineAndIntroduceParameterAsync(compilation,
                            document, originalDocument, invocationExpressionList, methodSymbol, containingMethod,
                            allOccurrences, parameterName, expression, trampoline, overload, cancellationToken).ConfigureAwait(false);
                        modifiedSolution = modifiedSolution.WithDocumentSyntaxRoot(originalDocument.Id, newRoot);
                    }
                    else
                    {
                        var newRoot = await ModifyDocumentInvocationsAndIntroduceParameterAsync(compilation,
                            originalDocument, document, mappingDictionary, methodSymbol, containingMethod,
                            expression, allOccurrences, parameterName, variablesInExpression, invocationExpressionList,
                            cancellationToken).ConfigureAwait(false);
                        modifiedSolution = modifiedSolution.WithDocumentSyntaxRoot(originalDocument.Id, newRoot);
                    }
                }
            }

            return modifiedSolution;
        }

        /// <summary>
        /// For the trampoline case, it goes through the invocations and adds an argument which is a 
        /// call to the extracted method.
        /// Introduces a new method overload or new trampoline method.
        /// Updates the original method site with a newly introduced parameter.
        /// </summary>
        private static async Task<SyntaxNode> ModifyDocumentInvocationsTrampolineAndIntroduceParameterAsync(
            Compilation compilation, Document currentDocument, Document originalDocument,
            List<TInvocationExpressionSyntax> invocations, IMethodSymbol methodSymbol, SyntaxNode containingMethod,
            bool allOccurrences, string parameterName, TExpressionSyntax expression, bool trampoline, bool overload,
            CancellationToken cancellationToken)
        {
            var generator = SyntaxGenerator.GetGenerator(currentDocument);
            var semanticFacts = currentDocument.GetRequiredLanguageService<ISemanticFactsService>();
            var invocationSemanticModel = await currentDocument.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            var root = await currentDocument.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var editor = new SyntaxEditor(root, generator);
            var syntaxFacts = currentDocument.GetRequiredLanguageService<ISyntaxFactsService>();
            var insertionIndex = GetInsertionIndex(compilation, methodSymbol, syntaxFacts, containingMethod);
            var newMethodIdentifier = methodSymbol.Name + "_" + parameterName;

            if (trampoline)
            {
                foreach (var invocationExpression in invocations)
                {
                    editor.ReplaceNode(invocationExpression, (currentInvocation, _) =>
                    {
                        return GenerateNewInvocationExpressionForTrampoline(syntaxFacts, generator, currentInvocation,
                            invocationExpression, newMethodIdentifier, insertionIndex);
                    });
                }
            }

            // If you are at the original document, then also introduce the new method and introduce the parameter.
            if (currentDocument.Id == originalDocument.Id)
            {
                if (trampoline)
                {
                    var newMethodNode = await ExtractMethodAsync(originalDocument, expression, methodSymbol, newMethodIdentifier,
                        generator, cancellationToken).ConfigureAwait(false);
                    editor.InsertBefore(containingMethod, newMethodNode);
                }
                if (overload)
                {
                    var newMethodNode = await GenerateNewMethodOverloadAsync(originalDocument, expression, methodSymbol,
                        generator, cancellationToken).ConfigureAwait(false);
                    editor.InsertBefore(containingMethod, newMethodNode);
                }

                await UpdateExpressionInOriginalFunctionAsync(originalDocument, expression, containingMethod,
                    parameterName, editor, allOccurrences, cancellationToken).ConfigureAwait(false);
                var parameterType = invocationSemanticModel.GetTypeInfo(expression, cancellationToken).ConvertedType ?? invocationSemanticModel.Compilation.ObjectType;
                var refKind = syntaxFacts.GetRefKindOfArgument(expression);
                var parameter = generator.ParameterDeclaration(parameterName, generator.TypeExpression(parameterType), refKind: refKind);
                editor.InsertParameter(containingMethod, insertionIndex, parameter);
            }
            return editor.GetChangedRoot();

            // Adds an argument which is an invocation of the newly created method to the callsites
            // of the method invocations where a parameter was added.
            // Example:
            // public void M(int x, int y)
            // {
            //     int f = x * y; // highlight this expression
            // }
            // 
            // public void InvokeMethod()
            // {
            //     M(5, 6);
            // }
            //
            // ---------------------------------------------------->
            // 
            // public int M_f(int x, int y)
            // {
            //     return x * y;
            // }
            // 
            // public void M(int x, int y)
            // {
            //     int f = x * y;
            // }
            //
            // public void InvokeMethod()
            // {
            //     M(5, 6, M_f(5, 6)); // This is the generated invocation which is a new argument at the call site
            // }
            static TInvocationExpressionSyntax GenerateNewInvocationExpressionForTrampoline(ISyntaxFactsService syntaxFacts,
                SyntaxGenerator generator, SyntaxNode currentInvocation, TInvocationExpressionSyntax invocationExpression,
                string newMethodIdentifier, int insertionIndex)
            {
                var invocationArguments = syntaxFacts.GetArgumentsOfInvocationExpression(currentInvocation);
                var methodName = generator.IdentifierName(newMethodIdentifier);
                var expressionFromInvocation = syntaxFacts.GetExpressionOfInvocationExpression(invocationExpression);
                var newMethodInvocation = generator.InvocationExpression(methodName, invocationArguments);
                var allArguments = invocationArguments.Insert(insertionIndex, newMethodInvocation);
                return (TInvocationExpressionSyntax)generator.InvocationExpression(expressionFromInvocation, allArguments);
            }
        }

        /// <summary>
        /// Generates a method declaration containing a return expression of the highlighted expression.
        /// Example:
        /// public void M(int x, int y)
        /// {
        ///     int f = [|x * y|];
        /// }
        /// 
        /// ---------------------------------------------------->
        /// 
        /// public int M_f(int x, int y)
        /// {
        ///     return x * y;
        /// }
        /// 
        /// public void M(int x, int y)
        /// {
        ///     int f = x * y;
        /// }
        /// </summary>
        private static async Task<SyntaxNode> ExtractMethodAsync(Document document, TExpressionSyntax expression,
            IMethodSymbol methodSymbol, string newMethodIdentifier, SyntaxGenerator generator, CancellationToken cancellationToken)
        {
            // Remove trailing trivia because it adds spaces to the beginning of the following statement.
            var newStatements = ImmutableArray.CreateBuilder<SyntaxNode>();
            newStatements.Add(generator.ReturnStatement(expression.WithoutTrailingTrivia()));

            var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var typeSymbol = semanticModel.GetTypeInfo(expression, cancellationToken).ConvertedType ?? semanticModel.Compilation.ObjectType;
            var codeGenerationService = document.GetRequiredLanguageService<ICodeGenerationService>();
            var newMethod = CodeGenerationSymbolFactory.CreateMethodSymbol(methodSymbol, name: newMethodIdentifier, statements: newStatements.ToImmutable(), returnType: typeSymbol);
            var options = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);
            var newMethodDeclaration = codeGenerationService.CreateMethodDeclaration(newMethod, options: new CodeGenerationOptions(options: options, parseOptions: expression.SyntaxTree.Options));
            return newMethodDeclaration;
        }

        /// <summary>
        /// Generates a method declaration containing a call to the method that introduced the parameter.
        /// Example:
        /// 
        /// ***This is an intermediary step in which the original function has not be updated yet
        /// public void M(int x, int y)
        /// {
        ///     int f = [|x * y|];
        /// }
        /// 
        /// ---------------------------------------------------->
        /// 
        /// public void M(int x, int y) // Generated overload
        /// {
        ///     M(x, y, x * y);
        /// }
        /// 
        /// public void M(int x, int y) // Original function
        /// {
        ///     int f = x * y;
        /// }
        /// </summary>
        private static async Task<SyntaxNode> GenerateNewMethodOverloadAsync(Document document, TExpressionSyntax expression, IMethodSymbol methodSymbol, SyntaxGenerator generator, CancellationToken cancellationToken)
        {
            var arguments = generator.CreateArguments(methodSymbol.Parameters);

            // Remove trailing trivia because it adds spaces to the beginning of the following statement.
            arguments = arguments.Add(generator.Argument(expression.WithoutTrailingTrivia()));
            var memberName = methodSymbol.IsGenericMethod
                ? generator.GenericName(methodSymbol.Name, methodSymbol.TypeArguments)
                : generator.IdentifierName(methodSymbol.Name);
            var newStatements = ImmutableArray.CreateBuilder<SyntaxNode>();
            var invocation = generator.InvocationExpression(memberName, arguments);

            if (!methodSymbol.ReturnsVoid)
            {
                newStatements.Add(generator.ReturnStatement(invocation));
            }
            else
            {
                newStatements.Add(invocation);
            }

            var codeGenerationService = document.GetRequiredLanguageService<ICodeGenerationService>();
            var options = await document.GetOptionsAsync(cancellationToken).ConfigureAwait(false);
            var newMethod = CodeGenerationSymbolFactory.CreateMethodSymbol(methodSymbol, statements: newStatements.ToImmutable());
            var newMethodDeclaration = codeGenerationService.CreateMethodDeclaration(newMethod, options: new CodeGenerationOptions(options: options, parseOptions: expression.SyntaxTree.Options));
            return newMethodDeclaration;
        }

        /// <summary>
        /// This method goes through all the invocation sites and adds a new argument with the expression to be added.
        /// It also introduces a parameter at the original method site.
        /// 
        /// Example:
        /// public void M(int x, int y)
        /// {
        ///     int f = [|x * y|];
        /// }
        /// 
        /// public void InvokeMethod()
        /// {
        ///     M(5, 6);
        /// }
        /// 
        /// ---------------------------------------------------->
        /// 
        /// public void M(int x, int y, int f) // parameter gets introduced
        /// {
        /// }
        /// 
        /// public void InvokeMethod()
        /// {
        ///     M(5, 6, 5 * 6); // argument gets added to callsite
        /// }
        /// </summary>
        private async Task<SyntaxNode> ModifyDocumentInvocationsAndIntroduceParameterAsync(Compilation compilation,
            Document originalDocument, Document document, Dictionary<TIdentifierNameSyntax, IParameterSymbol> mappingDictionary,
            IMethodSymbol methodSymbol, SyntaxNode containingMethod, TExpressionSyntax expression,
            bool allOccurrences, string parameterName, IEnumerable<TIdentifierNameSyntax> variablesInExpression,
            List<TInvocationExpressionSyntax> invocations, CancellationToken cancellationToken)
        {
            var generator = SyntaxGenerator.GetGenerator(document);
            var semanticFacts = document.GetRequiredLanguageService<ISemanticFactsService>();
            var root = await document.GetRequiredSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var editor = new SyntaxEditor(root, generator);
            var syntaxFacts = document.GetRequiredLanguageService<ISyntaxFactsService>();
            var insertionIndex = GetInsertionIndex(compilation, methodSymbol, syntaxFacts, containingMethod);
            var invocationSemanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            foreach (var invocationExpression in invocations)
            {
                var expressionEditor = new SyntaxEditor(expression, generator);
                var invocationArguments = syntaxFacts.GetArgumentsOfInvocationExpression(invocationExpression);
                var parameterToArgumentMap = MapParameterToArgumentsAtInvocation(semanticFacts, invocationArguments, invocationSemanticModel, cancellationToken);
                editor.ReplaceNode(invocationExpression, (currentInvocation, _) =>
                {
                    var updatedInvocationArguments = syntaxFacts.GetArgumentsOfInvocationExpression(currentInvocation);
                    var parameterIsNamed = false;

                    var updatedExpression = CreateNewArgumentExpression(expressionEditor,
                        syntaxFacts, mappingDictionary, variablesInExpression, parameterToArgumentMap, updatedInvocationArguments);

                    var expressionFromInvocation = syntaxFacts.GetExpressionOfInvocationExpression(invocationExpression);
                    var allArguments = AddArgumentToArgumentList(updatedInvocationArguments, generator,
                        updatedExpression.WithAdditionalAnnotations(Formatter.Annotation), insertionIndex, parameterName, parameterIsNamed);
                    var newInvo = editor.Generator.InvocationExpression(expressionFromInvocation, allArguments);
                    return newInvo;
                });
            }

            // If you are at the original document, then also introduce the new method and introduce the parameter.
            if (document.Id == originalDocument.Id)
            {
                await UpdateExpressionInOriginalFunctionAsync(originalDocument, expression, containingMethod,
                    parameterName, editor, allOccurrences, cancellationToken).ConfigureAwait(false);
                var parameterType = invocationSemanticModel.GetTypeInfo(expression, cancellationToken).ConvertedType ??
                    invocationSemanticModel.Compilation.ObjectType;
                var refKind = syntaxFacts.GetRefKindOfArgument(expression);
                var parameter = generator.ParameterDeclaration(name: parameterName,
                    type: generator.TypeExpression(parameterType), refKind: refKind);
                editor.InsertParameter(containingMethod, insertionIndex, parameter);
            }

            return editor.GetChangedRoot();
        }

        /// <summary>
        /// This method iterates through the variables in the expression and maps the variables back to the parameter
        /// it is associated with. It then maps the parameter back to the argument at the invocation site and gets the
        /// index to retrieve the updated arguments at the invocation.
        /// </summary>
        private TExpressionSyntax CreateNewArgumentExpression(SyntaxEditor editor, ISyntaxFactsService syntaxFacts,
            Dictionary<TIdentifierNameSyntax, IParameterSymbol> mappingDictionary,
            IEnumerable<TIdentifierNameSyntax> variables,
            ImmutableDictionary<IParameterSymbol, int> parameterToArgumentMap,
            SeparatedSyntaxList<SyntaxNode> updatedInvocationArguments)
        {
            foreach (var variable in variables)
            {
                if (mappingDictionary.TryGetValue(variable, out var mappedParameter))
                {
                    var parameterMapped = false;
                    if (parameterToArgumentMap.TryGetValue(mappedParameter, out var index))
                    {
                        var updatedInvocationArgument = updatedInvocationArguments.ToArray()[index];
                        var argumentExpression = syntaxFacts.GetExpressionOfArgument(updatedInvocationArgument);
                        var parenthesizedArgumentExpression = editor.Generator.AddParentheses(argumentExpression, includeElasticTrivia: false);
                        editor.ReplaceNode(variable, parenthesizedArgumentExpression);
                        parameterMapped = true;
                    }

                    if (mappedParameter.HasExplicitDefaultValue && !parameterMapped)
                    {
                        var generatedExpression = GenerateExpressionFromOptionalParameter(mappedParameter);
                        var parenthesizedGeneratedExpression = editor.Generator.AddParentheses(generatedExpression, includeElasticTrivia: false);
                        editor.ReplaceNode(variable, parenthesizedGeneratedExpression);
                    }
                    /*
                    var parameterMapped = false;
                    var oldNode = expression.GetCurrentNode(variable); // <---------- this throws if the nodes aren't tracked
                    RoslynDebug.AssertNotNull(oldNode);
                    for (var i = 0; i < invocationArguments.Count; i++)
                    {
                        var semanticFacts = document.GetRequiredLanguageService<ISemanticFactsService>();
                        var argumentParameter = semanticFacts.FindParameterForArgument(invocationSemanticModel, invocationArguments.ToArray()[i], cancellationToken);
                        if (argumentParameter.Equals(mappedParameter, SymbolEqualityComparer.Default))
                        {
                            var updatedInvocationArgument = updatedInvocationArguments.ToArray()[i];
                            var argumentExpression = syntaxFacts.GetExpressionOfArgument(updatedInvocationArgument);
                            var parenthesizedArgumentExpression = generator.AddParentheses(argumentExpression, includeElasticTrivia: false);
                            expression = expression.ReplaceNode(oldNode, parenthesizedArgumentExpression);
                            parameterMapped = true;
                            break;
                        }
                    }

                    // This is special cased for optional parameters: if the invocation does not have an argument 
                    // corresponding to the optional parameter, then it generates an expression from the optional parameter.
                    if (mappedParameter.HasExplicitDefaultValue && !parameterMapped)
                    {
                        var generatedExpression = GenerateExpressionFromOptionalParameter(mappedParameter);
                        var parenthesizedGeneratedExpression = generator.AddParentheses(generatedExpression, includeElasticTrivia: false);
                        expression = expression.ReplaceNode(oldNode, parenthesizedGeneratedExpression);
                    }
                    */
                }
            }

            return (TExpressionSyntax)editor.GetChangedRoot();
        }

        private static ImmutableDictionary<IParameterSymbol, int> MapParameterToArgumentsAtInvocation(
            ISemanticFactsService semanticFacts, SeparatedSyntaxList<SyntaxNode> arguments,
            SemanticModel invocationSemanticModel, CancellationToken cancellationToken)
        {
            var mapping = ImmutableDictionary.CreateBuilder<IParameterSymbol, int>();
            for (var i = 0; i < arguments.Count; i++)
            {
                var argumentParameter = semanticFacts.FindParameterForArgument(invocationSemanticModel, arguments.ToArray()[i], cancellationToken);
                if (argumentParameter is not null)
                {
                    mapping.Add(argumentParameter, i);
                }
            }

            return mapping.ToImmutable();
        }

        /// <summary>
        /// Ties the identifiers within the expression back to their associated parameter.
        /// </summary>
        public static async Task<Dictionary<TIdentifierNameSyntax, IParameterSymbol>> MapExpressionToParametersAsync(
            Document document, TExpressionSyntax expression, CancellationToken cancellationToken)
        {
            var nameToParameterDict = new Dictionary<TIdentifierNameSyntax, IParameterSymbol>();
            var variablesInExpression = expression.DescendantNodes().OfType<TIdentifierNameSyntax>();
            var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            foreach (var variable in variablesInExpression)
            {
                var symbol = semanticModel.GetSymbolInfo(variable, cancellationToken).Symbol;
                if (symbol is IParameterSymbol parameterSymbol)
                {
                    nameToParameterDict.Add(variable, parameterSymbol);
                }
            }

            return nameToParameterDict;
        }

        /// <summary>
        /// Determines if the expression is something that should have code actions displayed for it.
        /// Depends upon the identifiers in the expression mapping back to parameters.
        /// Does not handle params parameters.
        /// </summary>
        private static async Task<(bool isParameterized, bool hasOptionalParameter)> ShouldExpressionDisplayCodeActionAsync(
            Document document, TExpressionSyntax expression, IMethodSymbol methodSymbol,
            CancellationToken cancellationToken)
        {
            var variablesInExpression = expression.DescendantNodes().OfType<TIdentifierNameSyntax>();
            var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var hasOptionalParameter = false;

            foreach (var parameter in methodSymbol.Parameters)
            {
                if (parameter.HasExplicitDefaultValue)
                {
                    hasOptionalParameter = true;
                }
            }

            foreach (var variable in variablesInExpression)
            {
                var parameterSymbol = semanticModel.GetSymbolInfo(variable, cancellationToken).Symbol;
                if (parameterSymbol is not IParameterSymbol parameter)
                {
                    return (false, hasOptionalParameter);
                }
                if (parameter.IsParams)
                {
                    return (false, hasOptionalParameter);
                }
            }

            return (methodSymbol != null && methodSymbol.GetParameters().Any(), hasOptionalParameter);
        }

        /// <summary>
        /// Creates new code actions for each introduce parameter possibility.
        /// Does not create actions for overloads/trampoline if there are optional parameters or if the methodSymbol
        /// is a constructor.
        /// </summary>
        private async Task<ImmutableArray<CodeAction>> AddActionsAsync(Document document,
            TExpressionSyntax expression, IMethodSymbol methodSymbol, SyntaxNode containingMethod,
            CancellationToken cancellationToken)
        {
            using var _ = ArrayBuilder<CodeAction>.GetInstance(out var actionsBuilder);
            var (isParameterized, hasOptionalParameter) = await ShouldExpressionDisplayCodeActionAsync(document, expression, methodSymbol, cancellationToken).ConfigureAwait(false);
            if (isParameterized)
            {
                actionsBuilder.Add(CreateNewCodeAction(FeaturesResources.Introduce_parameter_for_0, allOccurrences: false, trampoline: false, overload: false));
                actionsBuilder.Add(CreateNewCodeAction(FeaturesResources.Introduce_parameter_for_all_occurrences_of_0, allOccurrences: true, trampoline: false, overload: false));

                if (!hasOptionalParameter && methodSymbol.MethodKind is not MethodKind.Constructor)
                {
                    actionsBuilder.Add(CreateNewCodeAction(
                        FeaturesResources.Extract_method_to_invoke_at_all_callsites_for_0, allOccurrences: false, trampoline: true, overload: false));
                    actionsBuilder.Add(CreateNewCodeAction(
                        FeaturesResources.Extract_method_to_invoke_at_all_callsites_for_all_occurrences_of_0, allOccurrences: true, trampoline: true, overload: false));

                    actionsBuilder.Add(CreateNewCodeAction(
                        FeaturesResources.Introduce_overload_with_new_parameter_for_0, allOccurrences: false, trampoline: false, overload: true));
                    actionsBuilder.Add(CreateNewCodeAction(
                         FeaturesResources.Introduce_overload_with_new_parameter_for_all_occurrences_of_0, allOccurrences: true, trampoline: false, overload: true));
                }
            }

            return actionsBuilder.ToImmutable();

            // Local function to create a code action with more ease
            MyCodeAction CreateNewCodeAction(string actionName,
                bool allOccurrences, bool trampoline,
                bool overload)
            {
                return new MyCodeAction(actionName, c => IntroduceParameterAsync(
                    document, expression, methodSymbol, containingMethod, allOccurrences, trampoline, overload, c));
            }
        }

        /// <summary>
        /// Finds the matches of the expression within the same block.
        /// </summary>
        protected static async Task<IEnumerable<TExpressionSyntax>> FindMatchesAsync(Document originalDocument,
            TExpressionSyntax expressionInOriginal, SyntaxNode withinNodeInCurrent,
            bool allOccurrences, CancellationToken cancellationToken)
        {
            var syntaxFacts = originalDocument.GetRequiredLanguageService<ISyntaxFactsService>();
            var originalSemanticModel = await originalDocument.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            var matches = from nodeInCurrent in withinNodeInCurrent.DescendantNodesAndSelf().OfType<TExpressionSyntax>()
                          where NodeMatchesExpression(originalSemanticModel, expressionInOriginal, nodeInCurrent, allOccurrences, cancellationToken)
                          select nodeInCurrent;
            return matches;
        }

        private static bool NodeMatchesExpression(SemanticModel originalSemanticModel,
            TExpressionSyntax expressionInOriginal, TExpressionSyntax nodeInCurrent, bool allOccurrences,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nodeInCurrent == expressionInOriginal)
            {
                return true;
            }

            if (!allOccurrences)
            {
                return false;
            }
            else
            {
                // Original expression and current node being semantically equivalent isn't enough when the original expression 
                // is a member access via instance reference (either implicit or explicit), the check only ensures that the expression
                // and current node are both backed by the same member symbol. So in this case, in addition to SemanticEquivalence check, 
                // we also check if expression and current node are both instance member access.
                //
                // For example, even though the first `c` binds to a field and we are introducing a local for it,
                // we don't want other references to that field to be replaced as well (i.e. the second `c` in the expression).
                //
                //  class C
                //  {
                //      C c;
                //      void Test()
                //      {
                //          var x = [|c|].c;
                //      }
                //  }

                var originalOperation = originalSemanticModel.GetOperation(expressionInOriginal, cancellationToken);
                if (originalOperation != null && IsInstanceMemberReference(originalOperation))
                {
                    var currentOperation = originalSemanticModel.GetOperation(nodeInCurrent, cancellationToken);
                    return currentOperation != null && IsInstanceMemberReference(currentOperation) && SemanticEquivalence.AreEquivalent(
                        originalSemanticModel, originalSemanticModel, expressionInOriginal, nodeInCurrent);
                }

                return SemanticEquivalence.AreEquivalent(
                    originalSemanticModel, originalSemanticModel, expressionInOriginal, nodeInCurrent);
            }

            static bool IsInstanceMemberReference(IOperation operation)
                => operation is IMemberReferenceOperation memberReferenceOperation &&
                    memberReferenceOperation.Instance?.Kind == OperationKind.InstanceReference;
        }

        private class MyCodeAction : SolutionChangeAction
        {
            public MyCodeAction(string title, Func<CancellationToken, Task<Solution>> createChangedSolution)
                : base(title, createChangedSolution)
            {
            }
        }
    }
}
