/**
 * The wire shapes of the hosted tenancy routes, read from the
 * cloud-tenant-boundary contracts rather than guessed.
 *
 * A tenant is ids, name, role and revision, never a secret. The login start
 * answers where to go and what to bring back; the callback itself is
 * server-side and has no client shape here.
 */
import type { ApiFailure } from '../background-jobs/types';

/** A refusal from the API, carrying the delivered envelope rather than a flattened message. */
export class TenancyApiError extends Error {
  readonly code: string;
  readonly status: number;
  readonly traceId: string;
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;

  constructor(failure: ApiFailure, status: number) {
    super(`${failure.code}: ${failure.message}`);
    this.name = 'TenancyApiError';
    this.code = failure.code;
    this.status = status;
    this.traceId = failure.traceId;
    this.fieldErrors = failure.fieldErrors ?? {};
  }
}

/** One tenant as served: ids, name, role and revision, never a secret. */
export interface Tenant {
  readonly id: string;
  readonly name: string;
  readonly revision: number;
  readonly role: string;
}

/** One membership as served: the tenant, the caller's role and the scope id. */
export interface TenantMembership {
  readonly tenantId: string;
  readonly name: string;
  readonly role: string;
  readonly scopeId: string;
}

/** The GET /api/hosted/tenants body. */
export interface TenantList {
  readonly tenants: readonly TenantMembership[];
}

/** The POST /api/hosted/login/start body: where to go and what to bring back. */
export interface LoginStart {
  readonly authorizationUrl: string;
  readonly state: string;
}
