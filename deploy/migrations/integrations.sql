DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'integrations') THEN
        CREATE SCHEMA integrations;
    END IF;
END $EF$;
CREATE TABLE IF NOT EXISTS integrations.__ef_migrations_history (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___ef_migrations_history" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM integrations.__ef_migrations_history WHERE "MigrationId" = '20261128171744_IntegrationsInitial') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'integrations') THEN
            CREATE SCHEMA integrations;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM integrations.__ef_migrations_history WHERE "MigrationId" = '20261128171744_IntegrationsInitial') THEN
    CREATE TABLE integrations.integration_instances (
        id uuid NOT NULL,
        tenant_id uuid NOT NULL,
        plugin_key character varying(64) NOT NULL,
        name character varying(64) NOT NULL,
        name_key character varying(64) GENERATED ALWAYS AS (lower(name)) STORED NOT NULL,
        config jsonb NOT NULL,
        config_version integer NOT NULL,
        secret_refs jsonb NOT NULL,
        scope jsonb NOT NULL,
        interval_seconds integer NOT NULL,
        state character varying(20) NOT NULL,
        state_reason character varying(500),
        validated_hash character varying(64),
        last_validation jsonb,
        last_success_at timestamp with time zone,
        last_run_at timestamp with time zone,
        next_run_at timestamp with time zone,
        run_token uuid,
        consecutive_failures integer NOT NULL,
        last_outcome character varying(20),
        last_failure character varying(20),
        last_failure_detail character varying(500),
        version integer NOT NULL,
        created_by character varying(256) NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_by character varying(256) NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_integration_instances PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM integrations.__ef_migrations_history WHERE "MigrationId" = '20261128171744_IntegrationsInitial') THEN
    CREATE TABLE integrations.integration_checkpoints (
        instance_id uuid NOT NULL,
        stream character varying(64) NOT NULL,
        watermark timestamp with time zone NOT NULL,
        cursor character varying(1024),
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_integration_checkpoints PRIMARY KEY (instance_id, stream),
        CONSTRAINT fk_integration_checkpoints_integration_instances_instance_id FOREIGN KEY (instance_id) REFERENCES integrations.integration_instances (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM integrations.__ef_migrations_history WHERE "MigrationId" = '20261128171744_IntegrationsInitial') THEN
    CREATE INDEX ix_integration_instances_state_next_run_at ON integrations.integration_instances (state, next_run_at);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM integrations.__ef_migrations_history WHERE "MigrationId" = '20261128171744_IntegrationsInitial') THEN
    CREATE UNIQUE INDEX ix_integration_instances_tenant_id_name_key ON integrations.integration_instances (tenant_id, name_key);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM integrations.__ef_migrations_history WHERE "MigrationId" = '20261128171744_IntegrationsInitial') THEN
    INSERT INTO integrations.__ef_migrations_history ("MigrationId", "ProductVersion")
    VALUES ('20261128171744_IntegrationsInitial', '10.0.15');
    END IF;
END $EF$;
COMMIT;

