namespace SimpleIPaaS.Infrastructure.Services.Email;

public class EmailOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = "no-reply@fusetor.com";
    public string FromName { get; set; } = "Fusetor";
}
