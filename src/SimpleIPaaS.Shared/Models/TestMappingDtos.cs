namespace SimpleIPaaS.Shared.Models;

public class TestMappingRequestDto
{
    public string MappingCode { get; set; } = string.Empty;
    public string FlowStateJson { get; set; } = "{}";
    public string HttpResponseJson { get; set; } = "{}";
    public string TargetProperty { get; set; } = string.Empty;
    public string StepType { get; set; } = string.Empty;
}

public class TestMappingResponseDto
{
    public bool Success { get; set; }
    public string Result { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}
