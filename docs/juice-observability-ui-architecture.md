# Juice Observability — Product & UI Architecture

**Status:** Approved 2026-11-01 (Phase 0 decision D8). Console: Angular with `juice-layout`, part of the MVP.  
**Purpose:** This document is the authoritative UI/product architecture context for implementation work, especially Track B. It clarifies the role of Juice Console versus Grafana.

---

## 1. Core Product Decision

**Juice Console is the primary and default user interface of Juice Observability.**

Grafana is **optional**. It is an integration surface for customers who already operate Grafana or explicitly want to use it.

This means:

- A standard Juice Observability deployment does **not** require Grafana.
- Users must be able to operate Juice Observability entirely through Juice Console.
- Juice domain models, APIs, health, discovery, rules, services, resources, traces, logs and metrics must be designed independently of Grafana.
- Grafana must never become the product's primary UX or a mandatory runtime dependency.
- Existing customer Grafana installations should be supported without forcing the customer to replace them.

### Product principle

> **Juice Console is the product UI. Grafana is an optional visualization integration.**

---

## 2. High-Level Architecture

```text
                         Juice Observability
                                |
             +------------------+------------------+
             |                                     |
       Juice Console                         Juice API
       Primary UI                        Domain / Control API
             |                                     |
             +------------------+------------------+
                                |
                    Observability Platform
             +------------------+------------------+
             |                  |                  |
         PostgreSQL         ClickHouse          Collector
       Platform state       Telemetry data       OTLP ingest
                                                    ^
                                                    |
                                              Applications
```

Optional external visualization:

```text
                    Customer Environment
                           |
              +------------+------------+
              |                         |
        Juice Console             Existing Grafana
          (primary)                  (optional)
              |                         |
              +------------+------------+
                           |
                      Juice integration
                           |
                    Observability data
```

Grafana is therefore **outside the core Juice product path**.

---

## 3. Architectural Layers

### 3.1 Data Plane

Responsible for collecting and storing observability telemetry.

```text
Application
    |
   OTLP
    |
Collector
    |
    +--> ClickHouse
```

Responsibilities:

- OTLP ingestion
- telemetry processing
- trace/log/metric storage
- retention
- data classification
- telemetry enrichment

The data plane should not depend on Grafana.

---

### 3.2 Juice Domain / Control Plane

Implemented primarily through Juice API and PostgreSQL.

Responsibilities include:

- Platform tenant
- Environment
- Application
- Service
- Instance
- Resource
- Dependency
- Health state
- Discovery/reconciliation
- Alert/rule definitions
- Configuration
- Platform metadata
- Access control / user-facing configuration

This layer represents the **Juice Observability domain**, not a Grafana-specific abstraction.

---

### 3.3 Juice API

Juice API is the boundary between the product UI and the observability platform.

```text
Juice Console
      |
      v
  Juice API
      |
 +----+-----------------------+
 |                            |
 v                            v
PostgreSQL                 ClickHouse
Platform state             Telemetry
```

The Console must not directly depend on ClickHouse schemas.

The Console should query Juice API for:

- service catalog
- environment/service state
- health
- resources
- dependencies
- alerts/rules
- trace/log/metric exploration
- dashboard-oriented data

Juice API may internally query PostgreSQL, ClickHouse, or other platform components.

This allows ClickHouse schemas and storage implementation to evolve without coupling the Console to them.

---

## 4. Juice Console

Juice Console is the **first-class product experience**.

Initial product areas should include:

```text
Juice Console
├── Overview
├── Applications
├── Services
├── Environments
├── Instances
├── Resources
├── Dependencies
├── Health
├── Traces
├── Logs
├── Metrics
├── Alerts / Rules
└── Configuration
```

The exact navigation can evolve, but these capabilities belong to Juice rather than being delegated to Grafana.

### Important UX principle

A customer installing Juice Observability for the first time should be able to:

1. discover services/resources;
2. understand health;
3. inspect dependencies;
4. drill into traces/logs/metrics;
5. configure rules/alerts;

without installing or operating Grafana.

---

## 5. Grafana Integration

Grafana is an **optional external integration**.

### Customer without existing Grafana

Recommended deployment:

```text
Juice Observability
├── Collector
├── ClickHouse
├── PostgreSQL
├── Juice API
└── Juice Console
```

No Grafana is required.

### Customer with existing Grafana

Recommended deployment:

```text
Customer
├── Existing Grafana
│       |
│       +---- Juice integration
│
└── Juice Observability
        ├── Collector
        ├── ClickHouse
        ├── PostgreSQL
        ├── Juice API
        └── Juice Console
```

The customer can continue using their existing Grafana while also using Juice Console.

---

## 6. Grafana Must Not Define the Juice Domain

Do **not** design Juice domain objects around Grafana concepts.

Bad direction:

```text
Juice Service
    -> Grafana Dashboard
    -> Grafana Panel
    -> Grafana Query
```

Preferred direction:

```text
Juice Service
    -> Health
    -> Dependencies
    -> Telemetry
    -> Rules
    -> Dashboard representation
```

Grafana can consume or visualize these concepts, but it does not own them.

Likewise, Juice APIs should not expose a Grafana-specific domain model merely because Grafana is one possible consumer.

---

## 7. Integration Boundary

The integration should be designed around stable interfaces.

Conceptually:

```text
                    Juice Observability
                           |
                      Juice API
                           |
             +-------------+-------------+
             |                           |
      Juice Console                 Integration API
       first-party                       |
                                         v
                                      Grafana
```

Possible integration mechanisms can be decided during implementation, but the architectural requirement is:

- Grafana access is read-oriented by default.
- Grafana integration must not make Grafana a runtime dependency of Juice.
- Removing Grafana must not break Juice Console.
- Adding Grafana later must not require redesigning Juice domain models.
- Customer-owned Grafana should remain customer-owned.

---

## 8. Dashboard Strategy

There are two distinct concepts:

### Juice-native dashboards

These are part of Juice Console and should be designed around Juice's domain model.

Examples:

- platform overview
- application overview
- service health
- dependency topology
- service performance
- trace/log/metric drill-down

### External Grafana dashboards

These are optional and primarily intended for:

- customers already standardized on Grafana;
- customers with existing dashboards;
- advanced/custom visualization needs;
- migration scenarios where customers do not want to replace their existing Grafana.

Juice should not assume that every dashboard must be represented as a Grafana dashboard.

---

## 9. Authentication and Authorization

Juice Console authenticates against the Juice platform's identity/access model.

Grafana may have its own authentication mechanism or customer IdP integration.

Do not make Juice authorization dependent on Grafana.

The direction is:

```text
Juice identity / permissions
          |
          +--> Juice Console

Customer Grafana identity
          |
          +--> Grafana
```

Where Grafana needs access to Juice data, expose the appropriate bounded read access rather than granting unrestricted access to the platform database.

---

## 10. Implications for Phase 0 Decisions

### D1 — Identifiers

Keep the existing direction:

- platform-generated UUIDv7 IDs;
- telemetry values are natural keys;
- service is environment-independent.

This is especially important because Juice Console needs a stable domain identity independent of telemetry storage or Grafana.

### D2 — Application tenant

Keep:

`juice.application_tenant_id`

as descriptive telemetry context.

It must remain distinct from the Juice platform tenant.

### D3 — Resource identity and reconciliation

Keep:

- natural keys;
- explicit aliases for renames;
- no automatic deletion of services/resources;
- inactive instances can be pruned according to policy.

This is foundational for the Console's service/resource catalog.

### D4 — Health

Health is a Juice domain capability and must be available through Juice API and Console.

It should not be delegated to Grafana alerting.

### D5 — Rules

Typed rule templates remain the recommended MVP approach.

Rules are Juice domain objects.

Grafana alerting may be supported later as an integration, but it must not become the source of truth for Juice health/rules.

### D6 — OTLP authentication

Static bearer tokens over TLS remain acceptable for the single-tenant MVP.

This is independent of whether Grafana is installed.

### D7 — Targets, classification, retention

Keep the existing retention and classification direction.

### D8 — Replace the previous Grafana-centric decision

D8 should be:

> **Console and external visualization integration**

Recommendation:

> Juice Console is the primary and default UI. Grafana is optional and supported as an external visualization/integration surface for customers who already operate Grafana.

The previous concept of “dedicated Grafana per deployment” must no longer be treated as a required product architecture.

Grafana versioning and isolation become implementation details of the optional integration.

### D9 — Ownership

Keep the ownership decision separate from UI architecture.

---

## 11. Non-Goals

For the MVP, do **not**:

- make Grafana mandatory;
- embed Grafana as the primary Juice UI;
- make Juice Console depend on Grafana;
- make ClickHouse schema directly visible to the Console;
- make Grafana dashboard definitions the source of truth for Juice services/health/rules;
- build a complete Grafana replacement on day one.

The goal is a **native Juice product experience**, while retaining Grafana as a compatibility/integration option.

---

## 12. Track B Implications

Track B should therefore build toward:

```text
                 Track B
                    |
       +------------+-------------+
       |                          |
   Juice Domain               Juice API
       |                          |
       +------------+-------------+
                    |
              Juice Console
```

The API should be designed first around Juice domain capabilities.

Do not start by asking:

> “What data does Grafana need?”

Instead ask:

> “What does Juice Observability need to expose as a product?”

Then add Grafana integration as a consumer of the appropriate data/interfaces.

---

## 13. Final Architectural Principle

The most important rule for future implementation decisions is:

> **Juice Observability must be a standalone observability product with its own Console, API, domain model and user experience. Grafana is an optional integration for customers who already have Grafana; it is never the product foundation or a mandatory dependency.**
