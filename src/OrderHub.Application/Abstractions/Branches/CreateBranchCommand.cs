namespace OrderHub.Application.Abstractions.Branches;

public sealed record CreateBranchCommand(string Name, string Address, bool IsActive);

