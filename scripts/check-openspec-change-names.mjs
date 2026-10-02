import { readdirSync, existsSync } from 'node:fs';
const root = new URL('../openspec/changes/', import.meta.url);
const invalid = existsSync(root) ? readdirSync(root, { withFileTypes: true }).filter(e => e.isDirectory() && e.name !== 'archive' && !e.name.startsWith('.') && !/^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$/.test(e.name)) : [];
for (const e of invalid) console.error(`Invalid OpenSpec change name: ${e.name}`);
process.exitCode = invalid.length ? 1 : 0;
