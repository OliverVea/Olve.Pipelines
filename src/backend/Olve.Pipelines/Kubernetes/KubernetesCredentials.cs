namespace Olve.Pipelines.Kubernetes;

public record KubernetesCredentials(string Token, string Server, string CaCertificate, string Namespace);
