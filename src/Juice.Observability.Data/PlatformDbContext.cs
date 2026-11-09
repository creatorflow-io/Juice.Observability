using System.Text.Json;
using System.Text.RegularExpressions;
using Juice.EF;
using Juice.Messaging.Outbox;
using Juice.Messaging.Outbox.EF;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Juice.Observability.Data
{
    /// <summary>
    /// The platform's PostgreSQL database. Every tenant-owned table carries <c>tenant_id</c>, and every natural-key
    /// uniqueness constraint includes it, so records of two tenants never merge (requirements §6).
    /// </summary>
    /// <remarks>
    /// The mediator's unit of work (Juice <see cref="UnitOfWork"/>) and the outbox's store (<see cref="IOutboxContext"/>): a command's changes, the domain
    /// events they raise and the integration events those publish are saved in one transaction by the TransactionBehavior, never by the command.
    /// </remarks>
    public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : UnitOfWork(options), IOutboxContext
    {
        public DbSet<OutboxEvent> Outbox => Set<OutboxEvent>();
        public DbSet<OutboxDelivery> OutboxDeliveries => Set<OutboxDelivery>();
        public DbSet<PlatformTenant> Tenants => Set<PlatformTenant>();
        public DbSet<Application> Applications => Set<Application>();
        public DbSet<DeploymentEnvironment> Environments => Set<DeploymentEnvironment>();
        public DbSet<Service> Services => Set<Service>();
        public DbSet<ServiceEnvironment> ServiceEnvironments => Set<ServiceEnvironment>();
        public DbSet<ServiceInstance> Instances => Set<ServiceInstance>();
        public DbSet<Resource> Resources => Set<Resource>();
        public DbSet<Dependency> Dependencies => Set<Dependency>();
        public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();
        public DbSet<ReconciliationCheckpoint> Checkpoints => Set<ReconciliationCheckpoint>();
        public DbSet<HealthSnapshot> HealthSnapshots => Set<HealthSnapshot>();
        public DbSet<HealthTransition> HealthTransitions => Set<HealthTransition>();
        public DbSet<Rule> Rules => Set<Rule>();
        public DbSet<Alert> Alerts => Set<Alert>();
        public DbSet<AlertTransition> AlertTransitions => Set<AlertTransition>();
        public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
        public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
        public DbSet<WebhookAllowlistEntry> WebhookAllowlist => Set<WebhookAllowlistEntry>();
        public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
        public DbSet<WebhookAttempt> WebhookAttempts => Set<WebhookAttempt>();
        public DbSet<LoadRun> LoadRuns => Set<LoadRun>();
        public DbSet<ChangeEvent> ChangeEvents => Set<ChangeEvent>();
        public DbSet<Silence> Silences => Set<Silence>();
        public DbSet<EscalationPolicy> EscalationPolicies => Set<EscalationPolicy>();
        public DbSet<AlertEscalation> AlertEscalations => Set<AlertEscalation>();
        public DbSet<Incident> Incidents => Set<Incident>();
        public DbSet<IncidentAlert> IncidentAlerts => Set<IncidentAlert>();
        public DbSet<IncidentEvent> IncidentEvents => Set<IncidentEvent>();
        public DbSet<EnrolledAgent> Agents => Set<EnrolledAgent>();
        public DbSet<AgentEnrollmentToken> AgentEnrollmentTokens => Set<AgentEnrollmentToken>();
        public DbSet<AgentCommand> AgentCommands => Set<AgentCommand>();
        public DbSet<ActionRequest> ActionRequests => Set<ActionRequest>();
        public DbSet<AgentEnrollmentFailure> AgentEnrollmentFailures => Set<AgentEnrollmentFailure>();

        /// <summary>Applies the snake_case naming used by every table. Call it when configuring the context.</summary>
        public static void Configure(DbContextOptionsBuilder options, string connectionString)
            => options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history"))
                .UseSnakeCaseNamingConvention();

        protected override void OnModelCreating(ModelBuilder model)
        {
            // The framework's outbox tables, in this database so they commit with the change that wrote them. It names the tables and writes its
            // partial-index filters for SQL Server ("[State] = 0"); here they follow this database: PostgreSQL, snake_case.
            this.ConfigureOutbox(model);
            model.Entity<OutboxEvent>().ToTable("outbox_events");
            model.Entity<OutboxDelivery>(e =>
            {
                e.ToTable("outbox_deliveries");
                foreach (var index in e.Metadata.GetIndexes())
                {
                    if (index.GetFilter() is { } filter)
                    {
                        index.SetFilter(Regex.Replace(filter, @"\[(\w+)\]", m => Regex.Replace(m.Groups[1].Value, "(?<!^)([A-Z])", "_$1").ToLowerInvariant()));
                    }
                }
            });

            model.Entity<PlatformTenant>(e =>
            {
                e.ToTable("platform_tenants");
                e.Property(x => x.Key).HasMaxLength(63);
                e.Property(x => x.DisplayName).HasMaxLength(200);
                e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
                e.HasIndex(x => x.Key).IsUnique();
            });

            model.Entity<Application>(e =>
            {
                e.ToTable("applications");
                Tenant(e);
                e.Property(x => x.Key).HasMaxLength(256);
                e.Property(x => x.DisplayName).HasMaxLength(256);
                e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
            });

            model.Entity<DeploymentEnvironment>(e =>
            {
                e.ToTable("environments");
                Tenant(e);
                e.Property(x => x.Key).HasMaxLength(256);
                e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
                e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
            });

            model.Entity<Service>(e =>
            {
                e.ToTable("services");
                Tenant(e);
                e.Property(x => x.Namespace).HasMaxLength(256);
                e.Property(x => x.Name).HasMaxLength(256);
                e.HasIndex(x => new { x.TenantId, x.Namespace, x.Name }).IsUnique();
                e.HasOne<Application>().WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.SetNull);
            });

            model.Entity<ServiceEnvironment>(e =>
            {
                e.ToTable("service_environments");
                e.HasKey(x => new { x.TenantId, x.ServiceId, x.EnvironmentId });
                Tenant(e);
                e.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<DeploymentEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
            });

            model.Entity<ServiceInstance>(e =>
            {
                e.ToTable("service_instances");
                Tenant(e);
                e.Property(x => x.InstanceKey).HasMaxLength(256);
                e.Property(x => x.KeySource).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.ServiceVersion).HasMaxLength(256);
                e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
                e.HasIndex(x => new { x.TenantId, x.ServiceId, x.EnvironmentId, x.InstanceKey }).IsUnique();
                e.HasIndex(x => new { x.TenantId, x.State, x.LastSeen });
                e.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<DeploymentEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne<Resource>().WithMany().HasForeignKey(x => x.HostResourceId).OnDelete(DeleteBehavior.SetNull);
            });

            model.Entity<Resource>(e =>
            {
                e.ToTable("resources");
                Tenant(e);
                e.Property(x => x.Type).HasConversion<string>().HasMaxLength(40);
                e.Property(x => x.Key).HasMaxLength(1024);
                e.Property(x => x.DisplayName).HasMaxLength(256);
                Json(e.Property(x => x.Attributes));
                e.HasIndex(x => new { x.TenantId, x.Type, x.Key }).IsUnique();
            });

            model.Entity<Dependency>(e =>
            {
                e.ToTable("dependencies", t => t.HasCheckConstraint("ck_dependencies_one_target",
                    "(target_service_id IS NULL) <> (target_resource_id IS NULL)"));
                Tenant(e);
                e.Property(x => x.Relation).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.Confidence).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.EvidenceSource).HasMaxLength(40);
                e.HasIndex(x => new { x.TenantId, x.EnvironmentId, x.SourceServiceId, x.Relation, x.TargetServiceId, x.TargetResourceId })
                    .IsUnique().AreNullsDistinct(false);
                e.HasOne<Service>().WithMany().HasForeignKey(x => x.SourceServiceId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<Service>().WithMany().HasForeignKey(x => x.TargetServiceId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<Resource>().WithMany().HasForeignKey(x => x.TargetResourceId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<DeploymentEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Restrict);
            });

            model.Entity<AuditRecord>(e =>
            {
                e.ToTable("audit_records");
                Tenant(e);
                e.Property(x => x.Actor).HasMaxLength(256);
                e.Property(x => x.Operation).HasMaxLength(100);
                e.Property(x => x.TargetType).HasMaxLength(100);
                e.Property(x => x.TargetId).HasMaxLength(256);
                e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.CorrelationId).HasMaxLength(128);
                Json(e.Property(x => x.Metadata));
                e.HasIndex(x => new { x.TenantId, x.OccurredAt });
            });

            model.Entity<ReconciliationCheckpoint>(e =>
            {
                e.ToTable("reconciliation_checkpoints");
                e.HasKey(x => new { x.TenantId, x.Source });
                e.Property(x => x.Source).HasMaxLength(100);
                e.HasOne<PlatformTenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            });

            ConfigureAlerts(model);
            ConfigureLoadRuns(model);
            ConfigureChanges(model);
            ConfigureWebhooks(model);
            ConfigureNotifications(model);
            ConfigureIncidents(model);
            ConfigureAgents(model);

            model.Entity<HealthSnapshot>(e =>
            {
                e.ToTable("health_snapshots");
                Tenant(e);
                e.Property(x => x.SubjectType).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.PolicyVersion).HasMaxLength(64);
                e.HasIndex(x => new { x.TenantId, x.SubjectType, x.SubjectId, x.EnvironmentId }).IsUnique().AreNullsDistinct(false);
                e.HasIndex(x => new { x.TenantId, x.State });
                e.HasOne<DeploymentEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
            });

            model.Entity<HealthTransition>(e =>
            {
                e.ToTable("health_transitions");
                Tenant(e);
                e.Property(x => x.SubjectType).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.FromState).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.ToState).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.PolicyVersion).HasMaxLength(64);
                e.HasIndex(x => new { x.TenantId, x.SubjectType, x.SubjectId, x.EnvironmentId, x.EvidenceTo, x.PolicyVersion })
                    .IsUnique().AreNullsDistinct(false);
                e.HasIndex(x => new { x.TenantId, x.SubjectId, x.EvaluatedAt });
                e.HasOne<DeploymentEnvironment>().WithMany().HasForeignKey(x => x.EnvironmentId).OnDelete(DeleteBehavior.Cascade);
            });
        }

        private static void ConfigureNotifications(ModelBuilder model)
        {
            model.Entity<Silence>(e =>
            {
                e.ToTable("silences");
                Tenant(e);
                e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.Comment).HasMaxLength(500);
                e.Property(x => x.Service).HasMaxLength(256);
                e.Property(x => x.Environment).HasMaxLength(256);
                e.Property(x => x.Host).HasMaxLength(256);
                e.Property(x => x.MaximumSeverity).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.CreatedBy).HasMaxLength(256);
                e.Property(x => x.CancelledBy).HasMaxLength(256);
                e.HasIndex(x => new { x.TenantId, x.EndsAt });
            });

            model.Entity<EscalationPolicy>(e =>
            {
                e.ToTable("escalation_policies");
                Tenant(e);
                e.Property(x => x.Key).HasMaxLength(63);
                e.Property(x => x.Name).HasMaxLength(200);
                e.Property(x => x.MinimumSeverity).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.Steps).HasColumnType("jsonb").HasConversion(new ValueConverter<List<EscalationStep>, string>(
                        v => JsonSerializer.Serialize(v, JsonOptions),
                        v => JsonSerializer.Deserialize<List<EscalationStep>>(v, JsonOptions) ?? new()),
                    new ValueComparer<List<EscalationStep>>(
                        (a, b) => JsonSerializer.Serialize(a, JsonOptions) == JsonSerializer.Serialize(b, JsonOptions),
                        v => JsonSerializer.Serialize(v, JsonOptions).GetHashCode(),
                        v => JsonSerializer.Deserialize<List<EscalationStep>>(JsonSerializer.Serialize(v, JsonOptions), JsonOptions)!));
                e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
            });

            model.Entity<AlertEscalation>(e =>
            {
                e.ToTable("alert_escalations");
                Tenant(e);
                e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.StopReason).HasMaxLength(40);
                e.HasIndex(x => new { x.AlertId, x.PolicyId }).IsUnique();
                e.HasIndex(x => new { x.State, x.NextAt });
                e.HasOne<Alert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<EscalationPolicy>().WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Restrict);
            });
        }

        private static void ConfigureIncidents(ModelBuilder model)
        {
            model.Entity<Incident>(e =>
            {
                e.ToTable("incidents");
                Tenant(e);
                e.Property(x => x.Title).HasMaxLength(200);
                e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.CreatedBy).HasMaxLength(256);
                e.HasIndex(x => new { x.TenantId, x.State });
            });

            model.Entity<IncidentAlert>(e =>
            {
                e.ToTable("incident_alerts");
                e.HasKey(x => new { x.IncidentId, x.AlertId });
                e.Property(x => x.AddedBy).HasMaxLength(256);
                e.HasIndex(x => x.AlertId);
                Tenant(e);
                e.HasOne<Incident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<Alert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Restrict);
            });

            model.Entity<IncidentEvent>(e =>
            {
                e.ToTable("incident_events");
                Tenant(e);
                e.Property(x => x.Actor).HasMaxLength(256);
                e.Property(x => x.Type).HasMaxLength(40);
                e.Property(x => x.FromState).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.ToState).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.Text).HasMaxLength(2000);
                e.HasIndex(x => new { x.IncidentId, x.At });
                e.HasOne<Incident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Cascade);
            });
        }

        private static void ConfigureAgents(ModelBuilder model)
        {
            model.Entity<EnrolledAgent>(e =>
            {
                e.ToTable("agents");
                Tenant(e);
                e.Property(x => x.Hostname).HasMaxLength(253);
                e.Property(x => x.OperatingSystem).HasMaxLength(200);
                e.Property(x => x.Version).HasMaxLength(64);
                e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.CertificateSerial).HasMaxLength(64);
                e.Property(x => x.PreviousCertificateSerial).HasMaxLength(64);
                e.Property(x => x.EnrolledBy).HasMaxLength(256);
                e.Property(x => x.RevokedBy).HasMaxLength(256);
                e.Property(x => x.RevokeReason).HasMaxLength(500);
                e.Property(x => x.Actions).HasColumnType("jsonb");
                e.HasIndex(x => x.CertificateSerial).IsUnique();
                e.HasIndex(x => new { x.TenantId, x.Status });
                e.HasOne<Resource>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.SetNull);
            });

            model.Entity<AgentEnrollmentFailure>(e =>
            {
                e.ToTable("agent_enrollment_failures");
                e.HasKey(x => x.Address);
                // An IPv6 address with a zone fits; anything longer isn't an address.
                e.Property(x => x.Address).HasMaxLength(64);
                e.HasIndex(x => x.WindowStart);
            });

            model.Entity<AgentEnrollmentToken>(e =>
            {
                e.ToTable("agent_enrollment_tokens");
                Tenant(e);
                e.Property(x => x.TokenHash).HasMaxLength(64);
                e.Property(x => x.Purpose).HasMaxLength(200);
                e.Property(x => x.CreatedBy).HasMaxLength(256);
                e.HasIndex(x => x.TokenHash).IsUnique();
                // Only one agent can use a token: the update that marks it used is conditional on it not being used yet.
                e.Property(x => x.UsedAt).IsConcurrencyToken();
            });

            model.Entity<AgentCommand>(e =>
            {
                e.ToTable("agent_commands");
                Tenant(e);
                e.Property(x => x.Action).HasMaxLength(64);
                e.Property(x => x.Parameters).HasColumnType("jsonb");
                e.Property(x => x.IssuedBy).HasMaxLength(256);
                e.Property(x => x.Nonce).HasMaxLength(128);
                e.Property(x => x.IdempotencyKey).HasMaxLength(128);
                e.Property(x => x.KeyId).HasMaxLength(64);
                e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.Reason).HasMaxLength(200);
                e.Property(x => x.Result).HasColumnType("jsonb");
                e.HasIndex(x => x.Nonce).IsUnique();
                e.HasIndex(x => new { x.AgentId, x.IdempotencyKey }).IsUnique();
                e.HasIndex(x => new { x.AgentId, x.State, x.IssuedAt });
                e.HasOne<EnrolledAgent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
            });

            model.Entity<ActionRequest>(e =>
            {
                e.ToTable("action_requests");
                Tenant(e);
                e.Property(x => x.Action).HasMaxLength(64);
                // Text, not jsonb: kept in the canonical form duplicates are compared by.
                e.Property(x => x.Parameters).HasMaxLength(CommandLimitsBytes);
                e.Property(x => x.Risk).HasMaxLength(10);
                e.Property(x => x.Policy).HasMaxLength(20);
                e.Property(x => x.Reason).HasMaxLength(500);
                e.Property(x => x.RequestedBy).HasMaxLength(256);
                e.Property(x => x.DecidedBy).HasMaxLength(256);
                e.Property(x => x.Comment).HasMaxLength(500);
                e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.TargetType).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.Verification).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.VerificationNote).HasMaxLength(500);
                e.HasIndex(x => new { x.TenantId, x.State, x.RequestedAt });
                e.HasIndex(x => x.Verification);
                e.HasIndex(x => new { x.AgentId, x.Action, x.State });
                e.HasIndex(x => x.CommandId);
                e.HasOne<EnrolledAgent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
            });
        }

        /// <summary>The most a command's signed payload holds, and so the most its parameters can be.</summary>
        private const int CommandLimitsBytes = 16 * 1024;

        private static void ConfigureChanges(ModelBuilder model)
        {
            model.Entity<ChangeEvent>(e =>
            {
                e.ToTable("change_events");
                Tenant(e);
                e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.Source).HasMaxLength(128);
                e.Property(x => x.Actor).HasMaxLength(200);
                e.Property(x => x.Summary).HasMaxLength(500);
                e.Property(x => x.Environment).HasMaxLength(128);
                e.Property(x => x.FromVersion).HasMaxLength(128);
                e.Property(x => x.ToVersion).HasMaxLength(128);
                e.Property(x => x.ExternalKey).HasMaxLength(256);
                Json(e.Property(x => x.Attributes));
                e.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.SetNull);
                e.HasOne<Resource>().WithMany().HasForeignKey(x => x.ResourceId).OnDelete(DeleteBehavior.SetNull);
                e.HasIndex(x => new { x.TenantId, x.OccurredAt });
                e.HasIndex(x => new { x.TenantId, x.ServiceId, x.OccurredAt });
                e.HasIndex(x => new { x.TenantId, x.Source, x.ExternalKey }).IsUnique().HasFilter("external_key IS NOT NULL");
            });
        }

        private static void ConfigureLoadRuns(ModelBuilder model)
        {
            model.Entity<LoadRun>(e =>
            {
                e.ToTable("load_runs");
                Tenant(e);
                e.Property(x => x.Name).HasMaxLength(128);
                e.Property(x => x.Environment).HasMaxLength(128);
                e.Property(x => x.StartedBy).HasMaxLength(200);
                e.HasIndex(x => new { x.TenantId, x.Environment });
            });
        }

        private static void ConfigureAlerts(ModelBuilder model)
        {
            model.Entity<Rule>(e =>
            {
                e.ToTable("rules");
                Tenant(e);
                e.Property(x => x.Key).HasMaxLength(63);
                e.Property(x => x.Name).HasMaxLength(200);
                e.Property(x => x.Template).HasConversion<string>().HasMaxLength(40);
                e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.Definition).HasColumnType("jsonb");
                e.Property(x => x.DefinitionHash).HasMaxLength(64);
                e.Property(x => x.ManagedBy).HasMaxLength(20).HasDefaultValue(ManagedBy.Configuration);
                e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
            });

            model.Entity<Alert>(e =>
            {
                e.ToTable("alerts");
                Tenant(e);
                e.Ignore(x => x.DomainEvents);
                e.Property(x => x.DedupKey).HasMaxLength(600);
                e.Property(x => x.SubjectKey).HasMaxLength(512);
                e.Property(x => x.SubjectType).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.State).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.AcknowledgedBy).HasMaxLength(256);
                e.Property(x => x.AcknowledgeComment).HasMaxLength(1000);
                Json(e.Property(x => x.Evidence));
                // Deduplication: one open alert per rule and subject.
                e.HasIndex(x => new { x.TenantId, x.DedupKey }).IsUnique().HasFilter("state <> 'Resolved'")
                    .HasDatabaseName("ux_alerts_open_dedup_key");
                e.HasIndex(x => new { x.TenantId, x.State });
                e.HasIndex(x => new { x.TenantId, x.DedupKey, x.ResolvedAt });
                e.HasOne<Rule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Restrict);
            });

            model.Entity<AlertTransition>(e =>
            {
                e.ToTable("alert_transitions");
                Tenant(e);
                e.Property(x => x.FromState).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.ToState).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.Reason).HasMaxLength(200);
                e.Property(x => x.NotificationsPlannedAt).IsConcurrencyToken();
                e.HasIndex(x => new { x.AlertId, x.At });
                e.HasOne<Alert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Cascade);
            });

            model.Entity<IdempotencyRecord>(e =>
            {
                e.ToTable("idempotency_records");
                e.HasKey(x => new { x.TenantId, x.Key, x.Operation });
                e.Property(x => x.Key).HasMaxLength(128);
                e.Property(x => x.Operation).HasMaxLength(200);
                e.Property(x => x.RequestHash).HasMaxLength(64);
                // text, not jsonb: a replay returns the first response byte for byte
                e.Property(x => x.ResponseBody).HasColumnType("text");
                e.HasIndex(x => x.CreatedAt);
                e.HasOne<PlatformTenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            });
        }

        private static void ConfigureWebhooks(ModelBuilder model)
        {
            model.Entity<WebhookEndpoint>(e =>
            {
                e.ToTable("webhook_endpoints");
                Tenant(e);
                e.Property(x => x.Key).HasMaxLength(63);
                e.Property(x => x.Url).HasMaxLength(2048);
                e.Property(x => x.SecretRef).HasMaxLength(512);
                e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20).HasDefaultValue(ChannelKind.Webhook);
                e.Property(x => x.MinimumSeverity).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.DefinitionHash).HasMaxLength(64);
                e.Property(x => x.ManagedBy).HasMaxLength(20).HasDefaultValue(ManagedBy.Configuration);
                e.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
            });

            model.Entity<WebhookAllowlistEntry>(e =>
            {
                e.ToTable("webhook_allowlist");
                e.HasKey(x => new { x.TenantId, x.Destination });
                e.Property(x => x.Destination).HasMaxLength(256);
                e.Property(x => x.ManagedBy).HasMaxLength(20).HasDefaultValue(ManagedBy.Configuration);
                e.HasOne<PlatformTenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            });

            model.Entity<WebhookDelivery>(e =>
            {
                e.ToTable("webhook_deliveries");
                Tenant(e);
                e.Property(x => x.Event).HasMaxLength(40);
                e.Property(x => x.Payload).HasColumnType("text");
                e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
                e.Property(x => x.LastError).HasMaxLength(500);
                e.HasIndex(x => new { x.Status, x.NextAttemptAt });
                e.HasIndex(x => new { x.TenantId, x.EndpointId, x.CreatedAt });
                // One delivery per transition, endpoint and escalation step, so a replayed evaluation or escalation cannot notify twice.
                e.HasIndex(x => new { x.TransitionId, x.EndpointId, x.EscalationStep }).IsUnique();
                e.HasIndex(x => new { x.TenantId, x.AlertId });
                // One per request, endpoint and event: a decision retried can't notify twice.
                e.HasIndex(x => new { x.ActionRequestId, x.EndpointId, x.Event }).IsUnique();
                e.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Restrict);
                e.HasOne<Alert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Cascade);
                e.HasOne<ActionRequest>().WithMany().HasForeignKey(x => x.ActionRequestId).OnDelete(DeleteBehavior.Cascade);
            });

            model.Entity<WebhookAttempt>(e =>
            {
                e.ToTable("webhook_attempts");
                Tenant(e);
                e.Property(x => x.Error).HasMaxLength(500);
                e.HasIndex(x => new { x.DeliveryId, x.Number }).IsUnique();
                e.HasOne<WebhookDelivery>().WithMany().HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Cascade);
            });
        }

        private static void Tenant<T>(EntityTypeBuilder<T> e) where T : class
            => e.HasOne<PlatformTenant>().WithMany().HasForeignKey("TenantId").OnDelete(DeleteBehavior.Restrict);

        private static readonly JsonSerializerOptions JsonOptions = new();

        private static void Json(PropertyBuilder<Dictionary<string, string>> property)
            => property.HasColumnType("jsonb").HasConversion(new ValueConverter<Dictionary<string, string>, string>(
                    v => JsonSerializer.Serialize(v, JsonOptions),
                    v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, JsonOptions) ?? new()),
                new ValueComparer<Dictionary<string, string>>(
                    (a, b) => a!.Count == b!.Count && !a.Except(b).Any(),
                    v => v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                    v => new Dictionary<string, string>(v)));
    }
}
