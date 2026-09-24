namespace TarsClient.Models;

/// <summary>One of TARS's knowledge or persona files from <c>GET /api/admin/files</c>.</summary>
/// <param name="Area"><c>knowledge</c> or <c>persona</c>.</param>
/// <param name="Name">File name, e.g. <c>people.md</c>.</param>
/// <param name="Bytes">Size.</param>
/// <param name="Modified">Last write, epoch seconds.</param>
/// <param name="Deletable">Whether the server allows deleting it.</param>
public sealed record ServerFile(string Area, string Name, long Bytes, double Modified, bool Deletable);
