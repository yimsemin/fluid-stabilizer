namespace FluidStabilizer.Contracts
{
    // Episode-lifetime evidence; connection state alone cannot describe a mixed run.
    public interface IPolicyExecutionStatus
    {
        string PolicyIdentity { get; }
        string ExecutionStatus { get; }
        string FallbackReason { get; }
        long PolicyTicks { get; }
        long FallbackTicks { get; }
        string PolicySettings { get; }
    }
}
