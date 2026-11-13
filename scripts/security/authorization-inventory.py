"""Generate current service and Razor route inventories for manual review."""
import csv
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parents[2]
SRC = ROOT / "src/Confast.Web"
OUT = ROOT / "artifacts/authorization-inventory"
OUT.mkdir(parents=True, exist_ok=True)

def write(name, rows):
    with (OUT / name).open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)

method_pattern = re.compile(r"^    (public|internal|private|protected) (?:static |async |override |sealed |virtual |partial )*[^\n;{}=]+?\b(\w+)\s*\(", re.M)
methods = []
for path in sorted((SRC / "Features").rglob("*.cs")):
    text = path.read_text(encoding="utf-8-sig")
    matches = list(method_pattern.finditer(text))
    for index, match in enumerate(matches):
        if match[1] not in ("public", "internal"):
            continue
        end = matches[index + 1].start() if index + 1 < len(matches) else len(text)
        body = text[match.start():end]
        guards = sorted(set(re.findall(r"Permissions\.\w+\.\w+|authorization\.\w+Async|permissions\.\w+Async|operation\.Require|AuthorizationMutationLock\.\w+Async|BusinessHttpAuthorization.Require", body)))
        calls = sorted(set(re.findall(r"\b(\w+Async)\(", body)) - {match[2]})
        methods.append(dict(source=path.relative_to(SRC).as_posix(), line=text[:match.start()].count("\n")+1,
                            method=match[2], visibility=match[1], guards="; ".join(guards), calls="; ".join(calls)))
write("current-methods.csv", methods)

routes = []
for path in sorted((SRC / "Components").rglob("*.razor")):
    text = path.read_text(encoding="utf-8-sig")
    for route in re.finditer(r'^@page\s+"([^"]+)"', text, re.M):
        attrs = re.findall(r"^@attribute (.*)$", text, re.M)
        routes.append(dict(source=path.relative_to(SRC).as_posix(), route=route[1], authorization="; ".join(attrs)))
write("current-routes.csv", routes)
print(f"{len(methods)} methods and {len(routes)} routes written to {OUT.relative_to(ROOT)}")
