﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

namespace BabyKusto.Core.InternalRepresentation
{
    internal class IRJoinOnClause
    {
        public IRJoinOnClause(IRRowScopeNameReferenceNode left, IRRowScopeNameReferenceNode right)
        {
            Left = left ?? throw new ArgumentNullException(nameof(left));
            Right = right ?? throw new ArgumentNullException(nameof(right));
        }

        public IRRowScopeNameReferenceNode Left { get; }
        public IRRowScopeNameReferenceNode Right { get; }

        public override string ToString()
        {
            return $"JoinOnClause({Left.ReferencedSymbol.Display}, {Right.ReferencedSymbol.Display})";
        }
    }
}