using System;

namespace SimpleIPaaS.Domain.Entities;

// Host-wide storage configuration: which backend serves each growing dataset, and how long
// its data is kept.
//
// Host-wide configuration. The API and the Engine must agree on where a dataset lives
// (they write and read the same indices), and an Elasticsearch index is a host-level object —
// a per-deployment setting would leave writes in a store the other process cannot find.
// There is exactly one row (Id = 1).
public sealed class StorageSettings
{
    public int Id { get; set; } = 1;

    // Master switch. When off every dataset uses the relational database, whatever the
    // per-dataset settings say — that keeps a half-configured cluster from being used.
    public bool ElasticsearchEnabled { get; set; }

    // e.g. http://localhost:9200
    public string ElasticsearchUrl { get; set; } = string.Empty;

    public string ElasticsearchUsername { get; set; } = string.Empty;

    public string EncryptedElasticsearchPassword { get; set; } = string.Empty;

    // Alternative to basic auth; when set it wins over username/password.
    public string EncryptedElasticsearchApiKey { get; set; } = string.Empty;

    // Prefix for every index this app owns: <prefix>-<dataset>-<period>.
    public string IndexPrefix { get; set; } = "fusetor";

    // Development convenience for self-signed cluster certificates.
    public bool AllowInvalidCertificate { get; set; }

    public int TimeoutSeconds { get; set; } = 15;

    // StorageDatasetSettings[] — a JSON column because the dataset list is expected to grow
    // (a new growing dataset should not need a schema migration on two providers).
    public string DatasetsJson { get; set; } = "[]";

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
