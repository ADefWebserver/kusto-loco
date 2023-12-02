﻿using System.Collections.Generic;

namespace BabyKusto.Core.Evaluation;

internal partial class TreeEvaluator
{
    private readonly record struct NpmSummarisedChunk(ITableChunk Chunk, List<int> RowIds);

    private readonly record struct NpmSummarySet(object?[] ByValues,
        List<NpmSummarisedChunk> SummarisedChunks);
}