﻿using System;

// ReSharper disable PartialTypeWithSinglePart
namespace BabyKusto.Core.Evaluation.BuiltIns.Impl;

[KustoImplementation(Keyword = "Functions.Radians")]
internal partial class RadiansFunction
{
    private static double Impl(double degrees) => Math.PI / 180 * degrees;
}