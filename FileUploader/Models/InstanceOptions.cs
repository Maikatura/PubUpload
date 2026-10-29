namespace FileUploader.Models;

/// <summary>
/// Per-instance branding and behaviour, bound from the "Instance" config section
/// (env vars: Instance__Name, Instance__Tagline, ...).
/// </summary>
public class InstanceOptions
{
    public const string SectionName = "Instance";

    public string Name { get; set; } = "PubUpload";
    public string Tagline { get; set; } = "Anonymous file hosting";
    public string Icon { get; set; } = "\U0001F4E4";
    public string FooterText { get; set; } = "anonymous file hosting";
    public int RetentionDays { get; set; } = 30;
}
