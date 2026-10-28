/* Conservative audit of only publishable files. No personal data directories are read. */
"use strict";
const fs = require("fs"),
  path = require("path");
const root = process.cwd();
const roots = [
  "src",
  "web",
  "assets",
  "scripts",
  "tests",
  "docs",
  "examples",
  ".github",
];
const files = [
  "README.md",
  "README.zh-CN.md",
  "LICENSE",
  "THIRD_PARTY_NOTICES.md",
  "CONTRIBUTING.md",
  "SECURITY.md",
  "CHANGELOG.md",
  "Build.cmd",
  ".gitignore",
  ".gitattributes",
  ".editorconfig",
  "design-qa.md",
];
function walk(p) {
  for (const e of fs.readdirSync(path.join(root, p), { withFileTypes: true })) {
    const name = path.join(p, e.name);
    if (e.isDirectory()) walk(name);
    else files.push(name);
  }
}
roots.forEach(walk);
const problems = [];
for (const file of files) {
  if (/\.(exe|dll|pdb|bak|zip|log)$/i.test(file))
    problems.push("Generated artifact: " + file);
  if (
    ["config.json", "tasks.json", "reminder-state.json"].includes(
      path.basename(file),
    )
  )
    problems.push("Runtime data: " + file);
  if (/\.(png|ico)$/i.test(file)) continue;
  const text = fs.readFileSync(path.join(root, file), "utf8");
  if (/-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----/.test(text))
    problems.push("Private key: " + file);
  if (/\bgh[pousr]_[A-Za-z0-9]{30,}\b/.test(text))
    problems.push("GitHub token: " + file);
  if (/\bsk-[A-Za-z0-9_-]{24,}\b/.test(text))
    problems.push("Potential API token: " + file);
  if (/[A-Z]:[\\/]Users[\\/]Administrator[\\/]/i.test(text))
    problems.push("Machine-specific personal path: " + file);
}
if (problems.length) {
  console.error(problems.join("\n"));
  process.exit(1);
}
console.log(
  "Source allowlist audit passed: " +
    files.length +
    " files. No runtime data/binaries or known credential patterns.",
);
