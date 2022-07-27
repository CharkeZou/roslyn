﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.IO;
using System.Linq;
using System.ServiceModel.Syndication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommonLanguageServerProtocol.Framework;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.UnitTests.Workspaces;
using Microsoft.CodeAnalysis.ExternalAccess.VSTypeScript;
using Microsoft.CodeAnalysis.ExternalAccess.VSTypeScript.Api;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Microsoft.CodeAnalysis.Test.Utilities;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Nerdbank.Streams;
using Roslyn.Test.Utilities;
using StreamJsonRpc;
using Xunit;

namespace Microsoft.CodeAnalysis.LanguageServer.UnitTests;
public class VSTypeScriptHandlerTests : AbstractLanguageServerProtocolTests
{
    protected override TestComposition Composition => base.Composition.AddParts(typeof(TypeScriptHandlerFactory));

    [Fact]
    public async Task TestExternalAccessTypeScriptHandlerInvoked()
    {
        var workspaceXml =
@$"<Workspace>
    <Project Language=""TypeScript"" CommonReferences=""true"" AssemblyName=""TypeScriptProj"">
        <Document FilePath=""C:\T.ts""></Document>
    </Project>
</Workspace>";

        using var testLspServer = await CreateTsTestLspServerAsync(workspaceXml);

        var document = testLspServer.GetCurrentSolution().Projects.Single().Documents.Single();
        var request = new TSRequest(document.GetURI(), ProtocolConversions.ProjectIdToProjectContextId(document.Project.Id));

        var response = await testLspServer.ExecuteRequestAsync<TSRequest, int>(TypeScriptHandler.MethodName, request, CancellationToken.None);
        Assert.Equal(TypeScriptHandler.Response, response);
    }

    [Fact]
    public async Task TestRoslynTypeScriptHandlerInvoked()
    {
        var workspaceXml =
@$"<Workspace>
    <Project Language=""TypeScript"" CommonReferences=""true"" AssemblyName=""TypeScriptProj"">
        <Document FilePath=""C:\T.ts""></Document>
    </Project>
</Workspace>";

        using var testLspServer = await CreateTsTestLspServerAsync(workspaceXml);
        testLspServer.TestWorkspace.GlobalOptions.SetGlobalOption(new OptionKey(InternalDiagnosticsOptions.NormalDiagnosticMode), DiagnosticMode.Pull);

        var document = testLspServer.GetCurrentSolution().Projects.Single().Documents.Single();
        var documentPullRequest = new VSInternalDocumentDiagnosticsParams
        {
            TextDocument = CreateTextDocumentIdentifier(document.GetURI(), document.Project.Id)
        };

        var response = await testLspServer.ExecuteRequestAsync<VSInternalDocumentDiagnosticsParams, VSInternalDiagnosticReport[]>(VSInternalMethods.DocumentPullDiagnosticName, documentPullRequest, CancellationToken.None);
        Assert.Empty(response);
    }

    private async Task<TestLspServer> CreateTsTestLspServerAsync(string workspaceXml)
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();
        var testWorkspace = TestWorkspace.Create(workspaceXml, composition: Composition);

        // Ensure workspace operations are completed so we don't get unexpected workspace changes while running.
        await WaitForWorkspaceOperationsAsync(testWorkspace);
        var languageServerTarget = CreateLanguageServer(serverStream, serverStream, testWorkspace);

        return await TestLspServer.CreateAsync(testWorkspace, new ClientCapabilities(), languageServerTarget, clientStream);
    }

    private static RoslynLanguageServerTarget CreateLanguageServer(Stream inputStream, Stream outputStream, TestWorkspace workspace)
    {
        var listenerProvider = workspace.ExportProvider.GetExportedValue<IAsynchronousOperationListenerProvider>();
        var capabilitiesProvider = workspace.ExportProvider.GetExportedValue<ExperimentalCapabilitiesProvider>();
        var servicesProvider = workspace.ExportProvider.GetExportedValue<VSTypeScriptLspServiceProvider>();

        var jsonRpc = new JsonRpc(new HeaderDelimitedMessageHandler(outputStream, inputStream))
        {
            ExceptionStrategy = ExceptionProcessing.ISerializable,
        };

        var logger = NoOpLspLogger.Instance;

        var languageServer = new RoslynLanguageServerTarget(
            servicesProvider, jsonRpc,
            capabilitiesProvider,
            listenerProvider,
            logger,
            ImmutableArray.Create(InternalLanguageNames.TypeScript),
            WellKnownLspServerKinds.RoslynTypeScriptLspServer);

        jsonRpc.StartListening();
        return languageServer;
    }

    internal record TSRequest(Uri Document, string Project);

    [ExportTypeScriptLspServiceFactory(typeof(TypeScriptHandler)), PartNotDiscoverable, Shared]
    internal class TypeScriptHandlerFactory : AbstractVSTypeScriptRequestHandlerFactory
    {
        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public TypeScriptHandlerFactory()
        {
        }

        protected override IVSTypeScriptRequestHandler CreateRequestHandler()
        {
            return new TypeScriptHandler();
        }
    }

    [VSTypeScriptMethod(MethodName)]
    internal class TypeScriptHandler : AbstractVSTypeScriptRequestHandler<TSRequest, int>
    {
        internal static int Response = 1;

        internal const string MethodName = "testMethod";

        protected override bool MutatesSolutionState => false;

        protected override bool RequiresLSPSolution => true;

        protected override TypeScriptTextDocumentIdentifier? GetTypeSciptTextDocumentIdentifier(TSRequest request)
        {
            return new TypeScriptTextDocumentIdentifier(request.Document, request.Project);
        }

        protected override Task<int> HandleRequestAsync(TSRequest request, TypeScriptRequestContext context, CancellationToken cancellationToken)
        {
            Assert.NotNull(context.Solution);
            AssertEx.NotNull(context.Document);
            Assert.Equal(context.Document.GetURI(), request.Document);
            return Task.FromResult(Response);
        }
    }
}
