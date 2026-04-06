PRAGMA foreign_keys = ON;

-- Base schema for PARAFactoNative

CREATE TABLE IF NOT EXISTS app_meta(
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS audit_events (
  audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
  ts TEXT NOT NULL,
  actor TEXT,
  event_type TEXT NOT NULL,
  entity TEXT NOT NULL,
  entity_id INTEGER,
  details_json TEXT
);

CREATE TABLE IF NOT EXISTS invoice_counters (
  year INTEGER NOT NULL,
  month INTEGER NOT NULL,
  last_seq INTEGER NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY(year, month)
);

CREATE TABLE IF NOT EXISTS patients(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  code3 TEXT,
  nom TEXT,
  prenom TEXT,
  niss TEXT,
  statut TEXT,
  mutuelle TEXT,
  adresse TEXT,
  cp TEXT,
  ville TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  referend TEXT,
  numero TEXT,
  rue TEXT,
  pays TEXT,
  mail TEXT,
  telephone TEXT,
  prenom_med_presc TEXT,
  nom_med_presc TEXT,
  code_medecin TEXT,
  date_prescription TEXT,
  date_accord TEXT,
  periode_accord TEXT,
  nomenclature TEXT,
  commentaire TEXT
);

CREATE TABLE IF NOT EXISTS tarifs(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  libelle TEXT NOT NULL,
  part_patient INTEGER NOT NULL,
  part_mutuelle INTEGER NOT NULL,
  is_active INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS seances(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  patient_id INTEGER NOT NULL,
  tarif_id INTEGER NOT NULL,
  date_iso TEXT NOT NULL,
  is_cash INTEGER NOT NULL DEFAULT 0,
  commentaire TEXT,
  part_patient INTEGER NOT NULL,
  part_mutuelle INTEGER NOT NULL,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  FOREIGN KEY(patient_id) REFERENCES patients(id) ON DELETE RESTRICT,
  FOREIGN KEY(tarif_id) REFERENCES tarifs(id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS invoices(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  invoice_no TEXT NOT NULL,
  kind TEXT NOT NULL,
  patient_id INTEGER,
  mutuelle TEXT,
  date_iso TEXT NOT NULL,
  total_cents INTEGER NOT NULL DEFAULT 0,
  paid_cents  INTEGER NOT NULL DEFAULT 0,
  status TEXT NOT NULL DEFAULT 'unpaid',
  ref_invoice_id INTEGER,
  reason TEXT,
  ref_doc TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  FOREIGN KEY(patient_id) REFERENCES patients(id) ON DELETE SET NULL,
  FOREIGN KEY(ref_invoice_id) REFERENCES invoices(id) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS invoice_lines(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  invoice_id INTEGER NOT NULL,
  label TEXT,
  qty INTEGER NOT NULL DEFAULT 1,
  unit_price_cents INTEGER NOT NULL DEFAULT 0,
  total_cents INTEGER NOT NULL DEFAULT 0,
  patient_part_cents INTEGER NOT NULL DEFAULT 0,
  mutuelle_part_cents INTEGER NOT NULL DEFAULT 0,
  date_iso TEXT,
  created_at TEXT NOT NULL DEFAULT (datetime('now')),
  FOREIGN KEY(invoice_id) REFERENCES invoices(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS payments(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  invoice_id INTEGER NOT NULL,
  paid_date TEXT NOT NULL,
  amount_cents INTEGER NOT NULL,
  method TEXT NOT NULL,
  reference TEXT,
  created_at TEXT NOT NULL,
  FOREIGN KEY(invoice_id) REFERENCES invoices(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS losses(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  invoice_id INTEGER NOT NULL,
  loss_date TEXT NOT NULL,
  amount_cents INTEGER NOT NULL,
  reason TEXT,
  created_at TEXT NOT NULL,
  FOREIGN KEY(invoice_id) REFERENCES invoices(id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS mutual_invoice_revisions(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  invoice_id INTEGER NOT NULL,
  revision_no INTEGER NOT NULL,
  changed_at TEXT NOT NULL,
  new_total_cents INTEGER NOT NULL,
  reason TEXT NOT NULL,
  reference_doc TEXT NOT NULL,
  created_at TEXT NOT NULL,
  FOREIGN KEY(invoice_id) REFERENCES invoices(id) ON DELETE CASCADE,
  UNIQUE(invoice_id, revision_no)
);

-- Default meta
INSERT OR IGNORE INTO app_meta(key, value) VALUES('schema_version', '10');
