﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis;

internal interface IMergeConflictHandler
{
    ImmutableArray<TextChange> CreateEdits(SourceText originalSourceText, ArrayBuilder<UnmergedDocumentChanges> unmergedChanges);
}
