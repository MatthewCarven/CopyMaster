namespace CopyMaster.Engine;

/// <summary>What a job does with its sources.</summary>
public enum CopyOperation
{
    /// <summary>Copy the sources into the destination; the sources stay.</summary>
    Copy,

    /// <summary>Move the sources into the destination: a rename within one volume,
    /// copy (then verify, if on) then delete across volumes.</summary>
    Move,
}
