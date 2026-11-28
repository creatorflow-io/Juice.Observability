// The deployment's tenant Id for the generated notifications-provisioning.json: JUICE_PLATFORM_TENANT_ID in the environment, else in the
// stack's .env text, else the local stacks' tenant. The pipeline accepts a file for that tenant only.
export const DEFAULT_TENANT = '0193f0aa-0000-7000-8000-0000000000a1';

export const tenantFromEnvText = text => text?.match(/^JUICE_PLATFORM_TENANT_ID=(\S+)/m)?.[1];

export const resolveTenant = (env, envFileText) => env.JUICE_PLATFORM_TENANT_ID ?? tenantFromEnvText(envFileText) ?? DEFAULT_TENANT;
