using MatDock.Core.Operations;

namespace MatDock.Web.Support;

/// <summary>What <c>_OperationConsole</c> shows: the operation, its heading and the name of the download.</summary>
public sealed record OperationConsoleModel(Operation Operation, string Title, string FileName);
