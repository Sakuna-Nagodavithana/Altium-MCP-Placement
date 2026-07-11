"""Schematic PDF -> component list OCR helper for the EasyEDA/Altium BOM Builder.

Invocation:
    python pdf_bom_ocr.py <input.pdf> [output.json]

Workflow:
    1. Rasterize every page of the PDF to a high-resolution PNG (pdf2image -> poppler).
    2. Run Tesseract OCR on each page (pytesseract).
    3. Scan the OCR text and any embedded text layer for:
         - LCSC part numbers (C followed by 4-8 digits, e.g. C2040, C2765186)
         - Designator + value pairs (R1 10k, C3 100nF, U2 STM32F103C8T6, ...)
    4. Emit one JSON object:
         {
           "source": "<pdf basename>",
           "pageCount": N,
           "components": [
             { "designator": "R1", "value": "10k", "lcsc": "C25744", "page": 1 },
             ...
           ],
           "warnings": [ ... ],
           "unmatched_lcsc": [ "C12345", ... ]
         }

Notes:
    - The PDF is the least reliable source (printed graphics, OCR noise). The caller
      (C# BOM Builder) always lets the user edit the resulting tick list before fetching
      from JLCPCB, so a few wrong characters here are recoverable.
    - Requires poppler (for pdf2image) and Tesseract OCR installed on the machine.
"""

from __future__ import annotations

import json
import re
import sys
from typing import Any


DESIGNATOR_PREFIXES = (
    "R", "C", "L", "D", "Q", "U", "J", "K", "T", "SW", "RV", "RT", "F",
    "LED", "BT", "P", "CN", "TP", "H", "IC",
)
LCSC_RE = re.compile(r"\bC\d{4,8}\b", re.IGNORECASE)
DESIGNATOR_RE = re.compile(
    r"\b(" + "|".join(DESIGNATOR_PREFIXES) + r")(\d{1,4})\b",
    re.IGNORECASE,
)

VALUE_TOKEN_RE = re.compile(
    r"(?i)\b("
    r"\d+(\.\d+)?\s*(?:m|u|n|p|f|k|meg|g|t)?\s*(?:ohm|ohms|\u03a9|\u2126|r|f|v|w|h|hz|farad|henry|volt|amp|a)?"
    r"|0\s*ohm|0r"
    r"|stm32[a-z0-9]+"
    r"|esp32[a-z0-9\-]*"
    r"|ams1117[a-z0-9\-]+"
    r"|mp1584[a-z0-9]*"
    r"|lm\s?[0-9]+[a-z]+"
    r"|tlv[0-9]+[a-z]*"
    r")\b"
)


def _extract_text_with_pdfplumber(path: str) -> list[tuple[int, str]]:
    """Best-effort embedded text extraction (no OCR) per page."""
    try:
        import pdfplumber
    except Exception:
        return []
    pages: list[tuple[int, str]] = []
    try:
        with pdfplumber.open(path) as pdf:
            for i, page in enumerate(pdf.pages, start=1):
                try:
                    txt = page.extract_text() or ""
                except Exception:
                    txt = ""
                pages.append((i, txt))
    except Exception as exc:
        sys.stderr.write(f"[pdf_bom_ocr] pdfplumber failed: {exc}\n")
    return pages


def _extract_text_with_ocr(path: str, dpi: int = 400) -> list[tuple[int, str]]:
    """Rasterize PDF and OCR each page. Requires poppler + tesseract."""
    try:
        from pdf2image import convert_from_path
        import pytesseract
        from PIL import Image
    except Exception as exc:
        sys.stderr.write(f"[pdf_bom_ocr] OCR dependencies missing: {exc}\n")
        return []

    pages_text: list[tuple[int, str]] = []
    try:
        images = convert_from_path(path, dpi=dpi)
    except Exception as exc:
        sys.stderr.write(f"[pdf_bom_ocr] pdf2image failed: {exc}\n")
        return []

    for i, img in enumerate(images, start=1):
        try:
            # Tesseract psm 11 = sparse text (good for schematics with many small labels).
            txt = pytesseract.image_to_string(img, config="--psm 11")
        except Exception as exc:
            sys.stderr.write(f"[pdf_bom_ocr] tesseract failed on page {i}: {exc}\n")
            txt = ""
        pages_text.append((i, txt))
    return pages_text


def _normalize_designator(prefix: str, number: str) -> str:
    return f"{prefix.upper()}{number}"


def _scan_page(page_no: int, text: str) -> list[dict[str, Any]]:
    """Find LCSC numbers and designator/value pairs on one page."""
    found: dict[str, dict[str, Any]] = {}

    for m in LCSC_RE.finditer(text):
        lcsc = m.group(0).upper()
        # attach to nearest preceding designator on the same line
        start = max(0, m.start() - 60)
        window = text[start:m.start()]
        dm = None
        for d in DESIGNATOR_RE.finditer(window):
            dm = d  # keep last match before the LCSC
        if dm is not None:
            desig = _normalize_designator(dm.group(1), dm.group(2))
            row = found.setdefault(desig, {"designator": desig, "value": None, "lcsc": lcsc, "page": page_no})
            row["lcsc"] = lcsc
        else:
            # LCSC with no designator context -> leave for caller to surface as unmatched
            found.setdefault(lcsc, {"designator": None, "value": None, "lcsc": lcsc, "page": page_no})

    # Pair designators with the closest value token
    # Strategy: walk through tokens line by line; for each designator, pick the next value-like token.
    for line in text.splitlines():
        desig_matches = list(DESIGNATOR_RE.finditer(line))
        if not desig_matches:
            continue
        for dm in desig_matches:
            prefix, number = dm.group(1), dm.group(2)
            desig = _normalize_designator(prefix, number)
            tail = line[dm.end():]
            vm = VALUE_TOKEN_RE.search(tail)
            value = vm.group(0).strip() if vm else None
            row = found.get(desig)
            if row is None:
                found[desig] = {"designator": desig, "value": value, "lcsc": None, "page": page_no}
            else:
                if row.get("value") is None and value:
                    row["value"] = value

    # Drop the bare-LCSC-only entries (no designator)
    components = []
    for desig, row in found.items():
        if not row["designator"]:
            continue
        components.append(row)
    return components


def parse_pdf(path: str) -> dict[str, Any]:
    pages: list[tuple[int, str]] = []
    pages.extend(_extract_text_with_pdfplumber(path))
    if not pages:
        pages = _extract_text_with_ocr(path)

    warnings: list[str] = []
    if not pages:
        warnings.append("No text layer and OCR unavailable (install poppler + tesseract).")
        return {
            "source": _basename(path),
            "pageCount": 0,
            "components": [],
            "warnings": warnings,
            "unmatched_lcsc": [],
        }

    all_components: dict[str, dict[str, Any]] = {}
    unmatched_lcsc: set[str] = set()
    for page_no, text in pages:
        page_components = _scan_page(page_no, text)
        for row in page_components:
            desig = row["designator"]
            if desig:
                existing = all_components.get(desig)
                if existing is None:
                    all_components[desig] = row
                else:
                    if not existing.get("lcsc") and row.get("lcsc"):
                        existing["lcsc"] = row["lcsc"]
                    if not existing.get("value") and row.get("value"):
                        existing["value"] = row["value"]
            elif row.get("lcsc"):
                unmatched_lcsc.add(row["lcsc"])

    components = sorted(all_components.values(), key=lambda r: _designator_sort_key(r["designator"]))
    return {
        "source": _basename(path),
        "pageCount": len(pages),
        "components": components,
        "warnings": warnings,
        "unmatched_lcsc": sorted(unmatched_lcsc),
    }


def _basename(path: str) -> str:
    import os
    return os.path.basename(path)


def _designator_sort_key(d: str):
    m = re.match(r"([A-Za-z]+)(\d+)", d or "")
    if not m:
        return (d or "", 0)
    return (m.group(1).upper(), int(m.group(2)))


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        sys.stderr.write("usage: pdf_bom_ocr.py <input.pdf> [output.json]\n")
        return 2
    pdf_path = argv[1]
    out_path = argv[2] if len(argv) > 2 else None
    result = parse_pdf(pdf_path)
    payload = json.dumps(result, indent=2, ensure_ascii=False)
    if out_path:
        with open(out_path, "w", encoding="utf-8") as f:
            f.write(payload)
        sys.stderr.write(f"[pdf_bom_ocr] wrote {out_path} ({len(result['components'])} components)\n")
    else:
        sys.stdout.write(payload + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
