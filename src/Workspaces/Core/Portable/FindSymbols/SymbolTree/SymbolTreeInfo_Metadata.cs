﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Utilities;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.FindSymbols
{
    internal partial class SymbolTreeInfo
    {
        private static string GetMetadataNameWithoutBackticks(MetadataReader reader, StringHandle name)
        {
            var typeName = reader.GetString(name);
            var index = typeName.IndexOf('`');
            typeName = index > 0 ? typeName.Substring(0, index) : typeName;
            return typeName;
        }

        private static Metadata GetMetadataNoThrow(PortableExecutableReference reference)
        {
            try
            {
                return reference.GetMetadata();
            }
            catch (Exception e) when (e is BadImageFormatException || e is IOException)
            {
                return null;
            }
        }

        /// <summary>
        /// this gives you SymbolTreeInfo for a metadata
        /// </summary>
        public static async Task<SymbolTreeInfo> GetInfoForMetadataReferenceAsync(
            Solution solution,
            PortableExecutableReference reference,
            bool loadOnly,
            CancellationToken cancellationToken)
        {
            var metadata = GetMetadataNoThrow(reference);
            if (metadata == null)
            {
                return null;
            }

            // Find the lock associated with this piece of metadata.  This way only one thread is
            // computing a symbol tree info for a particular piece of metadata at a time.
            var gate = s_metadataIdToGate.GetValue(metadata.Id, s_metadataIdToGateCallback);
            using (await gate.DisposableWaitAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                SymbolTreeInfo info;
                if (s_metadataIdToInfo.TryGetValue(metadata.Id, out info))
                {
                    return info;
                }

                info = await LoadOrCreateMetadataSymbolTreeInfoAsync(
                    solution, reference, loadOnly, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (info == null && loadOnly)
                {
                    return null;
                }

                return s_metadataIdToInfo.GetValue(metadata.Id, _ => info);
            }
        }

        private static Task<SymbolTreeInfo> LoadOrCreateMetadataSymbolTreeInfoAsync(
            Solution solution,
            PortableExecutableReference reference,
            bool loadOnly,
            CancellationToken cancellationToken)
        {
            var filePath = reference.FilePath;
            return LoadOrCreateAsync(
                solution,
                filePath,
                loadOnly,
                create: version => CreateMetadataSymbolTreeInfo(solution, version, reference, cancellationToken),
                keySuffix: "",
                getVersion: info => info._version,
                readObject: reader => ReadSymbolTreeInfo(reader, (version, nodes) => GetSpellCheckerTask(solution, version, filePath, nodes)),
                writeObject: (w, i) => i.WriteTo(w),
                cancellationToken: cancellationToken);
        }

        private static SymbolTreeInfo CreateMetadataSymbolTreeInfo(
            Solution solution, VersionStamp version,
            PortableExecutableReference reference,
            CancellationToken cancellationToken)
        {
            var creator = new MetadataInfoCreator(solution, version, reference, cancellationToken);
            return creator.Create();
        }

        private struct MetadataInfoCreator : IDisposable
        {
            private static Predicate<string> s_isNotNullOrEmpty = s => !string.IsNullOrEmpty(s);
            private static ObjectPool<List<string>> s_stringListPool = new ObjectPool<List<string>>(() => new List<string>());

            private readonly Solution _solution;
            private readonly VersionStamp _version;
            private readonly PortableExecutableReference _reference;
            private readonly CancellationToken _cancellationToken;

            private readonly OrderPreservingMultiDictionary<string, string> _inheritanceMap;
            private readonly OrderPreservingMultiDictionary<MetadataNode, MetadataNode> _parentToChildren;
            private readonly MetadataNode _rootNode;

            // The metadata reader for the current metadata in the PEReference.
            private MetadataReader _metadataReader;

            // The set of type definitions we've read out of the current metadata reader.
            private readonly List<MetadataDefinition> _allTypeDefinitions;
            
            public MetadataInfoCreator(
                Solution solution, VersionStamp version, PortableExecutableReference reference, CancellationToken cancellationToken)
            {
                _solution = solution;
                _version = version;
                _reference = reference;
                _cancellationToken = cancellationToken;
                _metadataReader = null;
                _allTypeDefinitions = new List<MetadataDefinition>();

                _inheritanceMap = OrderPreservingMultiDictionary<string, string>.GetInstance();
                _parentToChildren = OrderPreservingMultiDictionary<MetadataNode, MetadataNode>.GetInstance();
                _rootNode = MetadataNode.Allocate(name: "");
            }

            private static IEnumerable<ModuleMetadata> GetModuleMetadata(Metadata metadata)
            {
                if (metadata is AssemblyMetadata)
                {
                    return ((AssemblyMetadata)metadata).GetModules();
                }
                else if (metadata is ModuleMetadata)
                {
                    return SpecializedCollections.SingletonEnumerable((ModuleMetadata)metadata);
                }
                else
                {
                    return SpecializedCollections.EmptyEnumerable<ModuleMetadata>();
                }
            }

            internal SymbolTreeInfo Create()
            {
                foreach (var moduleMetadata in GetModuleMetadata(GetMetadataNoThrow(_reference)))
                {
                    try
                    {
                        _metadataReader = moduleMetadata.GetMetadataReader();
                    }
                    catch (BadImageFormatException)
                    {
                        continue;
                    }

                    // First, walk all the symbols from metadata, populating the parentToChilren
                    // map accordingly.
                    GenerateMetadataNodes();

                    // Now, once we populated the inital map, go and get all the inheritance 
                    // information for all the types in the metadata.  This may refer to 
                    // types that we haven't seen yet.  We'll add those types to the parentToChildren
                    // map accordingly.
                    PopulateInheritanceMap();

                    // Clear the set of type definitions we read out of this piece of metadata.
                    _allTypeDefinitions.Clear();
                }

                return CreateSymbolTreeInfo(GenerateUnsortedNodes());
            }

            public void Dispose()
            {
                // Return all the metadata nodes back to the pool so that they can be
                // used for the next PEReference we read.
                foreach (var kvp in _parentToChildren)
                {
                    foreach (var child in kvp.Value)
                    {
                        MetadataNode.Free(child);
                    }
                }

                MetadataNode.Free(_rootNode);

                _parentToChildren.Free();
                _inheritanceMap.Free();
            }

            private void GenerateMetadataNodes()
            {
                var globalNamespace = _metadataReader.GetNamespaceDefinitionRoot();
                var definitionMap = OrderPreservingMultiDictionary<string, MetadataDefinition>.GetInstance();
                try
                {
                    LookupMetadataDefinitions(globalNamespace, definitionMap);

                    foreach (var kvp in definitionMap)
                    {
                        GenerateMetadataNodes(_rootNode, kvp.Key, kvp.Value);
                    }
                }
                finally
                {
                    definitionMap.Free();
                }
            }

            private void GenerateMetadataNodes(
                MetadataNode parentNode,
                string nodeName,
                OrderPreservingMultiDictionary<string, MetadataDefinition>.ValueSet definitionsWithSameName)
            {
                if (!UnicodeCharacterUtilities.IsValidIdentifier(nodeName))
                {
                    return;
                }

                var childNode = MetadataNode.Allocate(nodeName);
                _parentToChildren.Add(parentNode, childNode);

                // Add all child members
                var definitionMap = OrderPreservingMultiDictionary<string, MetadataDefinition>.GetInstance();
                try
                {
                    foreach (var definition in definitionsWithSameName)
                    {
                        LookupMetadataDefinitions(definition, definitionMap);
                    }

                    foreach (var kvp in definitionMap)
                    {
                        GenerateMetadataNodes(childNode,kvp.Key, kvp.Value);
                    }
                }
                finally
                {
                    definitionMap.Free();
                }
            }

            private void LookupMetadataDefinitions(
                MetadataDefinition definition,
                OrderPreservingMultiDictionary<string, MetadataDefinition> definitionMap)
            {
                switch (definition.Kind)
                {
                    case MetadataDefinitionKind.Namespace:
                        LookupMetadataDefinitions(definition.Namespace, definitionMap);
                        break;
                    case MetadataDefinitionKind.Type:
                        LookupMetadataDefinitions(definition.Type, definitionMap);
                        break;
                }
            }

            private void LookupMetadataDefinitions(
                TypeDefinition typeDefinition,
                OrderPreservingMultiDictionary<string, MetadataDefinition> definitionMap)
            {
                // Only bother looking for extension methods in static types.
                if ((typeDefinition.Attributes & TypeAttributes.Abstract) != 0 &&
                    (typeDefinition.Attributes & TypeAttributes.Sealed) != 0)
                {
                    foreach (var child in typeDefinition.GetMethods())
                    {
                        var method = _metadataReader.GetMethodDefinition(child);
                        if ((method.Attributes & MethodAttributes.SpecialName) != 0 ||
                            (method.Attributes & MethodAttributes.RTSpecialName) != 0)
                        {
                            continue;
                        }

                        // SymbolTreeInfo is only searched for types and extension methods.
                        // So we don't want to pull in all methods here.  As a simple approximation
                        // we just pull in methods that have attributes on them.
                        if ((method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public &&
                            (method.Attributes & MethodAttributes.Static) != 0 &&
                            method.GetCustomAttributes().Count > 0)
                        {
                            var definition = new MetadataDefinition(
                                MetadataDefinitionKind.Member, _metadataReader.GetString(method.Name));

                            definitionMap.Add(definition.Name, definition);
                        }
                    }
                }

                foreach (var child in typeDefinition.GetNestedTypes())
                {
                    var type = _metadataReader.GetTypeDefinition(child);

                    // We don't include internals from metadata assemblies.  It's less likely that
                    // a project would have IVT to it and so it helps us save on memory.  It also
                    // means we can avoid loading lots and lots of obfuscated code in the case the
                    // dll was obfuscated.
                    if (IsPublic(type.Attributes))
                    {
                        var definition = MetadataDefinition.Create(_metadataReader, type);
                        definitionMap.Add(definition.Name, definition);
                        _allTypeDefinitions.Add(definition);
                    }
                }
            }

            private void LookupMetadataDefinitions(
                NamespaceDefinition namespaceDefinition,
                OrderPreservingMultiDictionary<string, MetadataDefinition> definitionMap)
            {
                foreach (var child in namespaceDefinition.NamespaceDefinitions)
                {
                    var definition = MetadataDefinition.Create(_metadataReader, child);
                    definitionMap.Add(definition.Name, definition);
                }

                foreach (var child in namespaceDefinition.TypeDefinitions)
                {
                    var typeDefinition = _metadataReader.GetTypeDefinition(child);
                    if (IsPublic(typeDefinition.Attributes))
                    {
                        var definition = MetadataDefinition.Create(_metadataReader, typeDefinition);
                        definitionMap.Add(definition.Name, definition);
                        _allTypeDefinitions.Add(definition);
                    }
                }
            }

            private static bool IsPublic(TypeAttributes attributes)
            {
                var masked = attributes & TypeAttributes.VisibilityMask;
                return masked == TypeAttributes.Public || masked == TypeAttributes.NestedPublic;
            }

            private void PopulateInheritanceMap()
            {
                foreach (var typeDefinition in _allTypeDefinitions)
                {
                    Debug.Assert(typeDefinition.Kind == MetadataDefinitionKind.Type);
                    PopulateInheritance(typeDefinition);
                }
            }

            private void PopulateInheritance(MetadataDefinition metadataTypeDefinition)
            {
                var derivedTypeDefinition = metadataTypeDefinition.Type;
                var interfaceImplHandles = derivedTypeDefinition.GetInterfaceImplementations();

                if (derivedTypeDefinition.BaseType.IsNil &&
                    interfaceImplHandles.Count == 0)
                {
                    return;
                }

                var derivedTypeSimpleName = metadataTypeDefinition.Name;

                PopulateInheritance(derivedTypeSimpleName, derivedTypeDefinition.BaseType);

                foreach (var interfaceImplHandle in interfaceImplHandles)
                {
                    if (!interfaceImplHandle.IsNil)
                    {
                        var interfaceImpl = _metadataReader.GetInterfaceImplementation(interfaceImplHandle);
                        PopulateInheritance(derivedTypeSimpleName, interfaceImpl.Interface);
                    }
                }
            }

            private void PopulateInheritance(
                string derivedTypeSimpleName,
                EntityHandle baseTypeOrInterfaceHandle)
            {
                if (baseTypeOrInterfaceHandle.IsNil)
                {
                    return;
                }

                var baseTypeNameParts = s_stringListPool.Allocate();
                try
                {
                    AddBaseTypeNameParts(baseTypeOrInterfaceHandle, baseTypeNameParts);
                    if (baseTypeNameParts.Count > 0 &&
                        baseTypeNameParts.TrueForAll(s_isNotNullOrEmpty))
                    {
                        var lastPart = baseTypeNameParts.Last();
                        if (!_inheritanceMap.Contains(lastPart, derivedTypeSimpleName))
                        {
                            _inheritanceMap.Add(baseTypeNameParts.Last(), derivedTypeSimpleName);
                        }

                        // The parent/child map may not know about this base-type yet (for example,
                        // if the base type is a reference to a type outside of this assembly).
                        // Add the base type to our map so we'll be able ot resolve it later if 
                        // requested. 
                        EnsureParentsAndChildren(baseTypeNameParts);
                    }
                }
                finally
                {
                    s_stringListPool.ClearAndFree(baseTypeNameParts);
                }
            }

            private void AddBaseTypeNameParts(
                EntityHandle baseTypeOrInterfaceHandle,
                List<string> simpleNames)
            {
                var provider = BaseNameProvider.Allocate(simpleNames);
                try
                {
                    if (baseTypeOrInterfaceHandle.Kind == HandleKind.TypeDefinition)
                    {
                        provider.GetTypeFromDefinition(
                            _metadataReader, (TypeDefinitionHandle)baseTypeOrInterfaceHandle, rawTypeKind: 0);
                    }
                    else if (baseTypeOrInterfaceHandle.Kind == HandleKind.TypeReference)
                    {
                        provider.GetTypeFromReference(
                            _metadataReader, (TypeReferenceHandle)baseTypeOrInterfaceHandle, rawTypeKind: 0);
                    }
                    else if (baseTypeOrInterfaceHandle.Kind == HandleKind.TypeSpecification)
                    {
                        provider.GetTypeFromSpecification(
                            _metadataReader, (TypeSpecificationHandle)baseTypeOrInterfaceHandle, rawTypeKind: 0);
                    }
                }
                finally
                {
                    BaseNameProvider.Free(provider);
                }
            }

            private void EnsureParentsAndChildren(List<string> simpleNames)
            {
                var currentNode = _rootNode;

                foreach (var simpleName in simpleNames)
                {
                    var childNode = GetOrCreateChildNode(currentNode, simpleName);
                    currentNode = childNode;
                }
            }

            private MetadataNode GetOrCreateChildNode(
               MetadataNode currentNode, string simpleName)
            {
                foreach (var childNode in _parentToChildren[currentNode])
                {
                    if (childNode.Name == simpleName)
                    {
                        // Found an existing child node.  Just return that and all 
                        // future parts off of it.
                        return childNode;
                    }
                }

                // Couldn't find a child node with this name.  Make a new node for
                // it and return that for all future parts to be added to.
                var newChildNode = MetadataNode.Allocate(simpleName);
                _parentToChildren.Add(currentNode, newChildNode);
                return newChildNode;
            }

            private List<Node> GenerateUnsortedNodes()
            {
                var unsortedNodes = new List<Node>
                {
                    new Node(name: "", parentIndex: Node.RootNodeParentIndex)
                };

                AddUnsortedNodes(unsortedNodes, parentNode: _rootNode, parentIndex: 0);

                return unsortedNodes;
            }

            private void AddUnsortedNodes(
                List<Node> unsortedNodes, MetadataNode parentNode, int parentIndex)
            {
                foreach (var child in _parentToChildren[parentNode])
                {
                    var childNode = new Node(child.Name, parentIndex);
                    var childIndex = unsortedNodes.Count;
                    unsortedNodes.Add(childNode);

                    AddUnsortedNodes(unsortedNodes, child, childIndex);
                }
            }

            private SymbolTreeInfo CreateSymbolTreeInfo(List<Node> unsortedNodes)
            {
                return SymbolTreeInfo.CreateSymbolTreeInfo(
                    _solution, _version, _reference.FilePath, unsortedNodes, _inheritanceMap);
            }
        }

        private class MetadataNode
        {
            public string Name { get; private set; }

            private MetadataNode()
            {
            }

            private static readonly ObjectPool<MetadataNode> s_pool = new ObjectPool<MetadataNode>(() => new MetadataNode());

            public static MetadataNode Allocate(string name)
            {
                var node = s_pool.Allocate();
                Debug.Assert(node.Name == null);
                node.Name = name;
                return node;
            }

            public static void Free(MetadataNode node)
            {
                Debug.Assert(node.Name != null);
                node.Name = null;
                s_pool.Free(node);
            }
        }

        private enum MetadataDefinitionKind
        {
            Namespace,
            Type,
            Member,
        }

        private struct MetadataDefinition
        {
            public string Name { get; }
            public MetadataDefinitionKind Kind { get; }

            public NamespaceDefinition Namespace { get; private set; }
            public TypeDefinition Type { get; private set; }

            public MetadataDefinition(MetadataDefinitionKind kind, string name)
                : this()
            {
                Kind = kind;
                Name = name;
            }

            public static MetadataDefinition Create(
                MetadataReader reader, NamespaceDefinitionHandle namespaceHandle)
            {
                var definition = reader.GetNamespaceDefinition(namespaceHandle);
                return new MetadataDefinition(
                    MetadataDefinitionKind.Namespace,
                    reader.GetString(definition.Name))
                {
                    Namespace = definition
                };
            }

            public static MetadataDefinition Create(
                MetadataReader reader, TypeDefinition definition)
            {
                string typeName = GetMetadataNameWithoutBackticks(reader, definition.Name);

                return new MetadataDefinition(
                    MetadataDefinitionKind.Type,
                    typeName)
                {
                    Type = definition
                };
            }
        }
    }
}