﻿namespace KustoLoco.Core.Evaluation.BuiltIns.Impl;

[KustoImplementation(Keyword = "padright")]
internal partial class PadRightFunction
{
    private static string Impl(string s,long n) => s.PadRight((int)n,' ');
    private static string WithCharImpl(string s,long n,string pad) => s.PadRight((int)n,pad.Length >0 ? pad[0]:' ');
}
