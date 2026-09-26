"""One-time setup: download and install the Argos Translate EN<->VI language
packages so translation can run fully offline afterwards (per spec mục 13, 17).
Run once: python install_packages.py
"""
import argostranslate.package

argostranslate.package.update_package_index()
available_packages = argostranslate.package.get_available_packages()

wanted = [("en", "vi"), ("vi", "en")]

for from_code, to_code in wanted:
    match = next(
        (p for p in available_packages if p.from_code == from_code and p.to_code == to_code),
        None,
    )
    if match is None:
        print(f"NOT FOUND: {from_code} -> {to_code}")
        continue
    print(f"Installing {from_code} -> {to_code} ({match.package_version})...")
    path = match.download()
    argostranslate.package.install_from_path(path)
    print(f"Installed {from_code} -> {to_code}")

print("Done.")
