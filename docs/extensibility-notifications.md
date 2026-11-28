# Notification types and channels for modules

Part of the module contract (`Juice.Observability.Extensibility`, additive within 2.x). Security: W-25. The code is `Juice.Observability.Notifications`
(`NotificationModuleExtensions`, in `ModuleNotifications.cs`).

A module declares its own notification types and adds channels to the embedded Juice.Notifications instance, through its builder, at composition.
The core's own types go through the same path.

```csharp
public void ConfigureServices(IModuleServicesBuilder module)
{
    module.AddNotificationTypes(new TypeDeclaration("observability.my-module.ping", "Ping", "my-module", null, null, null, ExternalRecipients: true,
        schema, CorrelationRules.Optional, null, null, templates));

    module.AddNotificationChannels(services =>
    {
        services.AddSingleton(new ChannelDefinition("my-module-pager", AddressKind.None, ChannelAudience.SharedTargets, ContentShape.Body, Passthrough: true, RepeatsSafely: true));
        services.AddSingleton<INotificationProvider, PagerProvider>();
    });
}
```

## Types

- **Key.** `observability.<module>.<name>`, or a *moved key* no module has claimed yet. The moved keys are `CoreMovedNotificationTypes.All`: the action
  approval (`observability.action.approval`) and the action request events (`observability.action.request.*`). The agents module claims them, with
  `AddNotificationTypes(ObservabilityTypes.ActionRequests())`, exactly as the core declared them before, so no new type version is written.
- **Refused** with a `ModuleCompositionException`, and nothing declared: a key outside the module's prefix that isn't an unclaimed moved key; a core type
  (`ObservabilityTypes.Core()`: `observability.alert.firing` and `observability.alert.resolved`), even for a module whose name makes the prefix fit; a key
  another module declared; a moved key claimed twice; the same key twice; more than 50 types in one module.
- **Declared at start.** Valid types are appended to the embedded options' `Types`, after the core's, in composition order, and declared with the
  platform's application when the pipeline starts, like the core's. The embedded `Types` of a host with no external module equal
  `ObservabilityTypes.All()` in order and content. A declaration that is unchanged is not a new version.
- **A bad type** (a schema or workflow the pipeline refuses) is not checked at composition: Juice.Notifications has no public pre-check yet. The
  pipeline's start fails instead, loudly: readiness reports `notifications`, the host stays up, and the dead-man alarm reports `not-started` when it is
  configured. Fix the declaration and restart.

## Channels

- `register` runs on a scratch service collection first. Every `ChannelDefinition` it adds must be a registered **instance** (not a factory, a type
  or a keyed service), with a key matching `^[a-z][a-z0-9-]{1,31}$`. The key can't be `webhook`, `email`, `inbox`, `slack` or `teams`, or one already
  registered (the core's, another module's, or twice in one call). Otherwise the call is refused and none of its registrations is added.
- The scratch descriptors are then copied into the host's services. The module's provider (`INotificationProvider`) is **trusted code**: it runs in
  the pipeline's process with the pipeline's access. A provider that sends over HTTP must do it through the platform's destination policy client
  (`SafeHttpClient`, which checks every address against `DestinationPolicy` and the allowlist), never a client of its own.
- A channel whose shared targets have addresses of their own kind needs its provider to implement `ITargetAddresses` (the pipeline's default rule for
  a target's address is a URL).
- The channel's key is a channel for the pipeline's routes and integrations (a provisioning file or the admin pages), like any other.

## What a module can't do

- Name a type or channel in another module's, or the core's, namespace; redefine a core type or a built-in channel.
- Use a builder that isn't the host's: the module's name is what the prefix rule rests on, so only the host's own builder is accepted.
