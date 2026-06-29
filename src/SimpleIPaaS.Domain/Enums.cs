namespace SimpleIPaaS.Domain;

public enum StepType { Mapping, Branch, HttpAction }
public enum AuthType { None, Basic, Bearer, ApiKey, OAuth2ClientCredentials, OAuth2AuthCode, Custom }
public enum FlowStatus { Draft, Active, Paused, Archived }
public enum TriggerType { Manual, Webhook, Cron, Polling }
public enum ConnectionStatus { Active, Error, Expired }
public enum ExecutionStatus { InProgress, Success, Failed, PartialSuccess }
public enum PaginationStyle { None, LinkHeader, Cursor, PageOffset }
