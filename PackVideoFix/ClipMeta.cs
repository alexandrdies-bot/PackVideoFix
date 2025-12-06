using System;

namespace PackVideoFix;

internal sealed class ClipMeta
{
    public string? Barcode { get; set; }
    public string? Station { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
    public string? Status { get; set; }
    public string? Reason { get; set; }
    public string? VideoPath { get; set; }
}
