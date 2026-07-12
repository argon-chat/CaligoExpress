// bun:sqlite adapter exposing the D1 surface, so the exact same worker code runs locally under Bun.
// Imported ONLY by local.ts — never by the Cloudflare bundle (which uses the real D1 binding).

import { Database } from "bun:sqlite";
import type { D1Like, D1StmtLike } from "./types.ts";

class BunStmt implements D1StmtLike {
  constructor(
    private readonly db: Database,
    private readonly sql: string,
    private readonly args: unknown[] = [],
  ) {}

  bind(...args: unknown[]): D1StmtLike {
    return new BunStmt(this.db, this.sql, args);
  }

  async all<T = Record<string, unknown>>(): Promise<{ results: T[] }> {
    return { results: this.db.query(this.sql).all(...(this.args as never[])) as T[] };
  }

  async first<T = Record<string, unknown>>(): Promise<T | null> {
    return (this.db.query(this.sql).get(...(this.args as never[])) as T) ?? null;
  }

  async run(): Promise<unknown> {
    return this.db.query(this.sql).run(...(this.args as never[]));
  }
}

export function openBunD1(path: string): D1Like {
  const db = new Database(path, { create: true });
  db.exec("PRAGMA journal_mode = WAL;");
  return {
    prepare: (sql: string) => new BunStmt(db, sql),
  };
}
