﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;

// ReSharper disable PartialTypeWithSinglePart
namespace BabyKusto.Core.Evaluation.BuiltIns.Impl;

[KustoImplementation(Keyword = "Operators.UnaryMinus")]
internal partial class UnaryMinusFunction
{
    private static int IntImpl(int a) => -a;
    private static long LongImpl(long a) => -a;
    private static double DoubleImpl(double a) => -a;
    private static TimeSpan TsImp(TimeSpan a) => -a;
}