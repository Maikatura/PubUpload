namespace FileUploader.Services;

public static class ShareXConfigBuilder
{
    public static object Build(string baseUrl, string instanceName) => new
    {
        Version = "12.4.1",
        Name = instanceName,
        DestinationType = "ImageUploader, FileUploader",
        RequestMethod = "POST",
        RequestURL = $"{baseUrl}/api/upload",
        Body = "MultipartFormData",
        FileFormName = "file",
        URL = "$json:url$",
        DeletionURL = "$json:deletionURL$"
    };
}
