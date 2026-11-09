DROP TABLE entries;
--> statement-breakpoint
CREATE TABLE items (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL,
  system TEXT NOT NULL,
  bonus INTEGER NOT NULL,
  rarity INTEGER NOT NULL,
  created_at INTEGER NOT NULL
);
--> statement-breakpoint
CREATE TABLE runs (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  course INTEGER NOT NULL,
  difficulty TEXT NOT NULL,
  phase TEXT NOT NULL,
  floor INTEGER NOT NULL DEFAULT 0,
  return_floor INTEGER NOT NULL,
  arousal REAL NOT NULL DEFAULT 0,
  sensitivity REAL NOT NULL DEFAULT 1.0,
  climaxes INTEGER NOT NULL DEFAULT 0,
  clothes TEXT NOT NULL DEFAULT '正常',
  effects_json TEXT NOT NULL DEFAULT '[]',
  equipped_ids_json TEXT NOT NULL DEFAULT '[]',
  pending_items_json TEXT NOT NULL DEFAULT '[]',
  pending_finish TEXT,
  result TEXT,
  gained_json TEXT NOT NULL DEFAULT '[]',
  lost_json TEXT NOT NULL DEFAULT '[]',
  started_at INTEGER NOT NULL,
  finished_at INTEGER
);
--> statement-breakpoint
CREATE TABLE events (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  run_id INTEGER NOT NULL,
  floor INTEGER NOT NULL,
  digest_json TEXT NOT NULL,
  text TEXT,
  created_at INTEGER NOT NULL
);
