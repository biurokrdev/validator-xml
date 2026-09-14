/** Kontrakty API Mass.Api (api/registered-numbers). */

export type PoolType = 'Domestic' | 'International';
export type PoolState = 'Available' | 'Reserved' | 'Used' | 'Cancelled';
export type StateAction = 'Reserve' | 'Use' | 'Release' | 'Cancel';

export const POOL_TYPES: readonly PoolType[] = ['Domestic', 'International'];
export const POOL_STATES: readonly PoolState[] = ['Available', 'Reserved', 'Used', 'Cancelled'];

export const POOL_TYPE_LABEL: Record<PoolType, string> = {
  Domestic: 'Krajowy (SSCC)',
  International: 'Zagraniczny (S10)',
};

export const POOL_STATE_LABEL: Record<PoolState, string> = {
  Available: 'Dostępny',
  Reserved: 'Zarezerwowany',
  Used: 'Użyty',
  Cancelled: 'Anulowany',
};

export const STATE_ACTION_LABEL: Record<StateAction, string> = {
  Reserve: 'Zarezerwuj',
  Use: 'Oznacz jako użyty',
  Release: 'Zwolnij',
  Cancel: 'Anuluj',
};

/** Akcje dozwolone w danym stanie (lustro reguł z encji RegisteredNumberPool). */
export const ALLOWED_ACTIONS: Record<PoolState, readonly StateAction[]> = {
  Available: ['Reserve', 'Use', 'Cancel'],
  Reserved: ['Use', 'Release', 'Cancel'],
  Used: [],
  Cancelled: [],
};

export interface RegisteredNumber {
  id: string;
  fullNumber: string;
  type: PoolType;
  serial: number;
  state: PoolState;
  editor: string;
  changeDate: string;
  iacDigit: number | null;
  kindDigit: number | null;
  serviceIndicator: string | null;
  countryCode: string | null;
}

export interface PagedResponse<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface ListQuery {
  type?: PoolType | null;
  state?: PoolState | null;
  search?: string | null;
  page?: number;
  pageSize?: number;
}

export interface RangeRequest {
  firstNumber: string;
  count?: number | null;
  lastNumber?: string | null;
}

export interface RangeCheckExisting {
  fullNumber: string;
  state: PoolState;
  editor: string;
  changeDate: string;
}

export interface RangeCheckResponse {
  type: PoolType;
  firstNumber: string;
  lastNumber: string;
  requested: number;
  new: number;
  alreadyExisting: number;
  hasDuplicates: boolean;
  isFullyDuplicated: boolean;
  existing: RangeCheckExisting[];
}

export interface RangeImportResponse {
  type: PoolType;
  firstNumber: string;
  lastNumber: string;
  requested: number;
  added: number;
  skipped: number;
}

export interface ValidationResponse {
  input: string | null;
  isFormatValid: boolean;
  isValid: boolean;
  fullNumber: string | null;
  type: PoolType | null;
  existsInPool: boolean | null;
  state: PoolState | null;
  error: string | null;
}

export interface PoolStatistics {
  type: PoolType;
  available: number;
  reserved: number;
  used: number;
  cancelled: number;
  total: number;
}

/** RFC 7807 zwracany przez API przy błędach. */
export interface ProblemDetails {
  title?: string;
  status?: number;
  detail?: string;
  errors?: Record<string, string[]>;
}
