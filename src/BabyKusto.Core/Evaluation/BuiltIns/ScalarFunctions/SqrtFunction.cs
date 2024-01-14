﻿using System;

// ReSharper disable PartialTypeWithSinglePart
namespace BabyKusto.Core.Evaluation.BuiltIns.Impl;

[KustoImplementation(Keyword = "Functions.Sqrt")]
internal partial class SqrtFunction
{
    private static double Impl(double input) => Math.Sqrt(input);
}