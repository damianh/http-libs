using System.ComponentModel.DataAnnotations;

namespace Docs;

public sealed class DocSchema
{
    [Required]
    public string Title { get; set; } = "";

    [Required]
    public string Description { get; set; } = "";

    public string? SidebarLabel { get; set; }
    public int Order { get; set; }
    public string Section { get; set; } = "";
    public List<string> Topics { get; set; } = [];
}
