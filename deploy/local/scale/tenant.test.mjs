// node --test deploy/local/scale/tenant.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DEFAULT_TENANT, resolveTenant, tenantFromEnvText } from './tenant.mjs';

const other = '0193f0aa-0000-7000-8000-0000000000b2';

test('a non-default tenant in .env is the one used', () => {
  assert.equal(resolveTenant({}, `A=1\nJUICE_PLATFORM_TENANT_ID=${other}\nB=2\n`), other);
  assert.notEqual(resolveTenant({}, `JUICE_PLATFORM_TENANT_ID=${other}`), DEFAULT_TENANT);
});
test('the environment wins over .env, and no setting gives the local default', () => {
  assert.equal(resolveTenant({ JUICE_PLATFORM_TENANT_ID: 'from-env' }, `JUICE_PLATFORM_TENANT_ID=${other}`), 'from-env');
  assert.equal(resolveTenant({}, 'A=1\n'), DEFAULT_TENANT);
  assert.equal(resolveTenant({}, undefined), DEFAULT_TENANT);
});
test('only a line that starts with the name counts', () => {
  assert.equal(tenantFromEnvText(`# JUICE_PLATFORM_TENANT_ID=${other}\nX_JUICE_PLATFORM_TENANT_ID=${other}`), undefined);
});
