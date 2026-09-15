"""Generate the printable MultiExplorer runtime architecture reference.

Run after Graphviz has rendered the PNG inputs in docs/images. The Markdown file in
docs is the canonical editable narrative; this script lays out its corresponding
printable PDF.
"""

from __future__ import annotations

from datetime import date
from pathlib import Path

from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import A4, landscape
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.pdfbase.pdfmetrics import stringWidth
from reportlab.platypus import Paragraph, Table, TableStyle
from reportlab.pdfgen import canvas
from reportlab.lib.utils import ImageReader


ROOT = Path(__file__).resolve().parents[1]
IMAGES = ROOT / "docs" / "images"
OUTPUT = ROOT / "docs" / "MultiExplorer-Application-Architecture.pdf"
TITLE = "MultiExplorer Application Architecture"
VERSION = "1.5.9"

NAVY = colors.HexColor("#17253D")
BLUE = colors.HexColor("#2563EB")
TEAL = colors.HexColor("#007589")
MUTED = colors.HexColor("#52657D")
BORDER = colors.HexColor("#CBD5E1")
PALE = colors.HexColor("#F4F7FB")


def paragraph_style(size: float = 9.2, leading: float = 13.2, color: colors.Color = NAVY) -> ParagraphStyle:
    return ParagraphStyle(
        "architecture",
        fontName="Helvetica",
        fontSize=size,
        leading=leading,
        textColor=color,
        alignment=TA_LEFT,
        spaceAfter=0,
    )


BODY = paragraph_style()
SMALL = paragraph_style(8.1, 10.4, MUTED)
TABLE_BODY = paragraph_style(7.6, 9.3)
TABLE_HEAD = ParagraphStyle("table-head", parent=TABLE_BODY, textColor=colors.white, fontName="Helvetica-Bold")


def draw_footer(pdf: canvas.Canvas, page_number: int, width: float) -> None:
    pdf.setStrokeColor(BORDER)
    pdf.setLineWidth(0.5)
    pdf.line(16 * mm, 13 * mm, width - 16 * mm, 13 * mm)
    pdf.setFont("Helvetica", 7.5)
    pdf.setFillColor(MUTED)
    pdf.drawString(16 * mm, 8.5 * mm, TITLE)
    right = f"Page {page_number}  |  {date.today():%d %B %Y}"
    pdf.drawRightString(width - 16 * mm, 8.5 * mm, right)


def draw_header(pdf: canvas.Canvas, page_number: int, heading: str, subtitle: str = "") -> float:
    width, height = pdf._pagesize
    pdf.setFillColor(NAVY)
    pdf.setFont("Helvetica-Bold", 21)
    pdf.drawString(16 * mm, height - 20 * mm, heading)
    if subtitle:
        pdf.setFillColor(MUTED)
        pdf.setFont("Helvetica", 9.2)
        pdf.drawString(16 * mm, height - 27 * mm, subtitle)
    draw_footer(pdf, page_number, width)
    return height - 34 * mm


def draw_paragraph(pdf: canvas.Canvas, text: str, x: float, y: float, width: float, style: ParagraphStyle = BODY) -> float:
    paragraph = Paragraph(text, style)
    _, height = paragraph.wrap(width, y)
    paragraph.drawOn(pdf, x, y - height)
    return y - height


def draw_section(pdf: canvas.Canvas, heading: str, body: str, x: float, y: float, width: float) -> float:
    pdf.setFillColor(BLUE)
    pdf.setFont("Helvetica-Bold", 13)
    pdf.drawString(x, y, heading)
    return draw_paragraph(pdf, body, x, y - 6 * mm, width)


def draw_image_fit(pdf: canvas.Canvas, path: Path, x: float, y_top: float, max_width: float, max_height: float) -> float:
    image = ImageReader(str(path))
    source_width, source_height = image.getSize()
    scale = min(max_width / source_width, max_height / source_height)
    width = source_width * scale
    height = source_height * scale
    pdf.drawImage(image, x + (max_width - width) / 2, y_top - height, width=width, height=height, mask="auto")
    return y_top - height


def draw_card(
    pdf: canvas.Canvas,
    x: float,
    y: float,
    width: float,
    height: float,
    title: str,
    body_lines: list[str],
    border: colors.Color,
    fill: colors.Color,
    title_color: colors.Color | None = None,
) -> None:
    pdf.setFillColor(fill)
    pdf.setStrokeColor(border)
    pdf.setLineWidth(1.1)
    pdf.roundRect(x, y, width, height, 2.4 * mm, fill=1, stroke=1)
    pdf.setFillColor(title_color or border)
    pdf.setFont("Helvetica-Bold", 9.8)
    pdf.drawCentredString(x + width / 2, y + height - 6.0 * mm, title)
    pdf.setFillColor(MUTED)
    pdf.setFont("Helvetica", 6.8)
    line_y = y + height - 10.0 * mm
    for line in body_lines:
        pdf.drawCentredString(x + width / 2, line_y, line)
        line_y -= 3.25 * mm


def draw_container(pdf: canvas.Canvas, x: float, y: float, width: float, height: float, title: str, border: colors.Color, fill: colors.Color) -> None:
    pdf.setFillColor(fill)
    pdf.setStrokeColor(border)
    pdf.setLineWidth(1.25)
    pdf.roundRect(x, y, width, height, 3.2 * mm, fill=1, stroke=1)
    pdf.setFillColor(border)
    pdf.setFont("Helvetica-Bold", 8.3)
    pdf.drawString(x + 4 * mm, y + height - 5.0 * mm, title)


def draw_arrow(
    pdf: canvas.Canvas,
    start_x: float,
    start_y: float,
    end_x: float,
    end_y: float,
    color: colors.Color,
    label: str = "",
    dashed: bool = False,
) -> None:
    from math import atan2, cos, sin

    pdf.setStrokeColor(color)
    pdf.setFillColor(color)
    pdf.setLineWidth(1.0)
    if dashed:
        pdf.setDash(3, 2)
    pdf.line(start_x, start_y, end_x, end_y)
    pdf.setDash()
    angle = atan2(end_y - start_y, end_x - start_x)
    arrow_size = 2.25 * mm
    left = angle + 2.65
    right = angle - 2.65
    path = pdf.beginPath()
    path.moveTo(end_x, end_y)
    path.lineTo(end_x + arrow_size * cos(left), end_y + arrow_size * sin(left))
    path.lineTo(end_x + arrow_size * cos(right), end_y + arrow_size * sin(right))
    path.close()
    pdf.drawPath(path, fill=1, stroke=0)
    if label:
        mid_x = (start_x + end_x) / 2
        mid_y = (start_y + end_y) / 2
        pdf.setFillColor(color)
        pdf.setFont("Helvetica", 6.4)
        pdf.drawCentredString(mid_x, mid_y + 1.6 * mm, label)


def draw_runtime_overview(pdf: canvas.Canvas) -> None:
    """Draw a readable, purpose-built component overview for the landscape cover page."""
    width, height = pdf._pagesize
    blue_fill = colors.HexColor("#E8F0FF")
    teal_fill = colors.HexColor("#E4F7F8")
    purple_fill = colors.HexColor("#F0ECFF")
    green_fill = colors.HexColor("#E7F6ED")
    amber_fill = colors.HexColor("#FFF4D9")

    ui_x, ui_y, ui_w, ui_h = 17 * mm, 70 * mm, 177 * mm, 93 * mm
    worker_x, worker_y, worker_w, worker_h = 202 * mm, 92 * mm, 76 * mm, 66 * mm
    draw_container(pdf, ui_x, ui_y, ui_w, ui_h, "PROCESS 1 - MultiExplorer.exe     WinForms UI process", NAVY, PALE)
    draw_container(pdf, worker_x, worker_y, worker_w, worker_h, "PROCESS 2 - one OperationHost per operation", colors.HexColor("#17834D"), colors.HexColor("#EEF9F2"))

    draw_card(pdf, 111 * mm, 170 * mm, 55 * mm, 11 * mm, "User", ["mouse, keyboard, tray, global hotkey"], BLUE, blue_fill)
    draw_card(pdf, 29 * mm, 141 * mm, 37 * mm, 15 * mm, "Program / lifecycle", ["single-instance mutex", "settings + theme bootstrap", "PerMonitorV2 DPI"], BLUE, blue_fill)
    draw_card(pdf, 75 * mm, 137 * mm, 56 * mm, 21 * mm, "MainForm", ["dual-pane layout and splitter", "tray, hotkey, status and exit policy", "application-wide coordination"], TEAL, teal_fill)
    draw_card(pdf, 140 * mm, 141 * mm, 43 * mm, 15 * mm, "Cross-cutting services", ["ThemeManager | SettingsManager", "StartupManager | AppLog | HotkeyDialog"], colors.HexColor("#7654CC"), purple_fill)
    draw_card(pdf, 27 * mm, 91 * mm, 46 * mm, 35 * mm, "Left PanelView", ["TabBar | PathBar | CommandBar", "ExplorerHost per tab (STA)", "filter overlay | details | preview"], TEAL, colors.white)
    draw_card(pdf, 79 * mm, 91 * mm, 46 * mm, 35 * mm, "Right PanelView", ["TabBar | PathBar | CommandBar", "ExplorerHost per tab (STA)", "filter overlay | details | preview"], TEAL, colors.white)
    draw_card(pdf, 132 * mm, 96 * mm, 49 * mm, 25 * mm, "OperationManager", ["writes requests and launches workers", "polls state, cancels, reports status"], colors.HexColor("#17834D"), green_fill)
    draw_card(pdf, 212 * mm, 128 * mm, 56 * mm, 16 * mm, "OperationHost", ["reads one request; initializes OLE", "reports progress"], colors.HexColor("#17834D"), colors.white)
    draw_card(pdf, 212 * mm, 101 * mm, 56 * mm, 19 * mm, "ShellFileOperation", ["Windows IFileOperation COM", "copy, move, recycle/permanent delete", "progress and cancellation probe"], colors.HexColor("#17834D"), green_fill)

    external_x, external_y, external_w, external_h = 17 * mm, 24 * mm, 261 * mm, 36 * mm
    draw_container(pdf, external_x, external_y, external_w, external_h, "WINDOWS PLATFORM AND USER DATA", NAVY, NAVY)
    external_cards = [
        (23, "Windows Shell", ["ExplorerBrowser, Shell views", "context menus, previews"]),
        (74, "File system", ["folders, files", "and Recycle Bin"]),
        (125, "Windows Registry", ["startup, Explorer preferences", "preview-handler registrations"]),
        (176, "Per-user data", ["settings.json, app.log", "operation request/state/cancel"]),
        (227, "QuickLook (optional)", ["separate running process", "SID-scoped named pipe"]),
    ]
    for x_mm, title, body in external_cards:
        draw_card(pdf, x_mm * mm, 29 * mm, 45 * mm, 19 * mm, title, body, BORDER, colors.white, NAVY)

    draw_arrow(pdf, 138.5 * mm, 170 * mm, 103 * mm, 158 * mm, BLUE, "interacts")
    draw_arrow(pdf, 66 * mm, 148.5 * mm, 75 * mm, 148.5 * mm, BLUE, "creates")
    draw_arrow(pdf, 103 * mm, 137 * mm, 50 * mm, 126 * mm, TEAL, "owns")
    draw_arrow(pdf, 103 * mm, 137 * mm, 102 * mm, 126 * mm, TEAL, "owns")
    draw_arrow(pdf, 131 * mm, 147 * mm, 140 * mm, 147 * mm, colors.HexColor("#7654CC"), "uses")
    draw_arrow(pdf, 128 * mm, 137 * mm, 156 * mm, 121 * mm, colors.HexColor("#17834D"), "coordinates")
    draw_arrow(pdf, 181 * mm, 109 * mm, 212 * mm, 136 * mm, colors.HexColor("#17834D"), "launch")
    draw_arrow(pdf, 240 * mm, 128 * mm, 240 * mm, 120 * mm, colors.HexColor("#17834D"), "executes")
    draw_arrow(pdf, 50 * mm, 91 * mm, 45 * mm, 48 * mm, BLUE, "Shell COM")
    draw_arrow(pdf, 102 * mm, 91 * mm, 96 * mm, 48 * mm, TEAL, "navigate")
    draw_arrow(pdf, 161 * mm, 96 * mm, 198 * mm, 48 * mm, colors.HexColor("#17834D"), "request/state", dashed=True)
    draw_arrow(pdf, 240 * mm, 101 * mm, 96 * mm, 48 * mm, colors.HexColor("#17834D"), "file operations")
    draw_arrow(pdf, 161 * mm, 141 * mm, 147 * mm, 48 * mm, colors.HexColor("#7654CC"), "registry")
    draw_arrow(pdf, 170 * mm, 141 * mm, 198 * mm, 48 * mm, colors.HexColor("#7654CC"), "settings/log")
    draw_arrow(pdf, 102 * mm, 91 * mm, 249 * mm, 48 * mm, colors.HexColor("#C26A00"), "named pipe", dashed=True)

    pdf.setFillColor(MUTED)
    pdf.setFont("Helvetica", 6.5)
    pdf.drawString(17 * mm, 17.5 * mm, "Solid arrows: direct calls / ownership     Dashed arrows: asynchronous file or pipe exchange")


def draw_explorer_hosting_overview(pdf: canvas.Canvas) -> None:
    """Draw the thread-affinity diagram at print-readable type sizes."""
    teal_fill = colors.HexColor("#E4F7F8")
    blue_fill = colors.HexColor("#EDF4FF")
    purple_fill = colors.HexColor("#F0ECFF")
    amber_fill = colors.HexColor("#FFF4D9")
    teal = colors.HexColor("#00869A")
    purple = colors.HexColor("#7654CC")
    amber = colors.HexColor("#C26A00")

    draw_container(pdf, 16 * mm, 177 * mm, 178 * mm, 75 * mm, "WinForms UI thread - MultiExplorer.exe", teal, colors.HexColor("#F1FAFA"))
    draw_card(pdf, 59 * mm, 226 * mm, 92 * mm, 17 * mm, "PanelView", ["TabBar | PathBar | CommandBar", "filter overlay | details | preview"], teal, teal_fill)
    draw_card(pdf, 45 * mm, 197 * mm, 120 * mm, 22 * mm, "ExplorerHost", ["one instance per tab; cached path and selection snapshots", "command, keyboard, drag/drop, navigation and view routing"], BLUE, blue_fill)
    draw_card(pdf, 25 * mm, 181 * mm, 76 * mm, 11 * mm, "PreviewPane", ["registered IPreviewHandler in a child HWND"], amber, amber_fill)
    draw_card(pdf, 109 * mm, 181 * mm, 76 * mm, 11 * mm, "DetailsPanel", ["Shell icon and file metadata"], amber, amber_fill)

    draw_container(pdf, 16 * mm, 43 * mm, 178 * mm, 124 * mm, "Dedicated BrowserThread - STA + OLE message pump", purple, colors.HexColor("#F5F2FF"))
    draw_card(pdf, 48 * mm, 137 * mm, 114 * mm, 19 * mm, "Work queue and MessageHook", ["Invoke for COM work; Post for non-blocking layout", "input classification before normal Shell dispatch"], purple, purple_fill)
    draw_card(pdf, 48 * mm, 101 * mm, 114 * mm, 21 * mm, "IExplorerBrowser", ["in-process COM object created in the browser-thread apartment", "native child HWND under ExplorerHost"], BLUE, blue_fill)
    draw_card(pdf, 48 * mm, 65 * mm, 114 * mm, 21 * mm, "Windows Shell view", ["SHELLDLL_DefView / DirectUI", "context menus, thumbnails, drag/drop and Shell accelerators"], BLUE, colors.white)

    draw_arrow(pdf, 105 * mm, 226 * mm, 105 * mm, 219 * mm, teal, "active tab host")
    draw_arrow(pdf, 70 * mm, 197 * mm, 63 * mm, 192 * mm, amber, "selection")
    draw_arrow(pdf, 140 * mm, 197 * mm, 147 * mm, 192 * mm, amber, "selection")
    draw_arrow(pdf, 105 * mm, 197 * mm, 105 * mm, 156 * mm, purple, "Invoke / Post")
    draw_arrow(pdf, 105 * mm, 137 * mm, 105 * mm, 122 * mm, purple, "marshalled commands")
    draw_arrow(pdf, 105 * mm, 101 * mm, 105 * mm, 86 * mm, BLUE, "creates / hosts")
    draw_arrow(pdf, 162 * mm, 75 * mm, 165 * mm, 207 * mm, BLUE, "navigation callbacks")


def draw_file_operation_ipc_overview(pdf: canvas.Canvas) -> None:
    """Draw the durable UI-to-worker handoff at print-readable type sizes."""
    teal = colors.HexColor("#00869A")
    teal_fill = colors.HexColor("#E4F7F8")
    green = colors.HexColor("#17834D")
    green_fill = colors.HexColor("#E7F6ED")
    amber = colors.HexColor("#C26A00")
    amber_fill = colors.HexColor("#FFF4D9")

    draw_container(pdf, 17 * mm, 91 * mm, 53 * mm, 60 * mm, "MultiExplorer.exe", teal, colors.HexColor("#F1FAFA"))
    draw_card(pdf, 22 * mm, 112 * mm, 43 * mm, 26 * mm, "OperationManager", ["validates requested paths", "writes request; launches host", "polls every 500 ms; cancel/status"], teal, teal_fill)

    draw_container(pdf, 81 * mm, 82 * mm, 47 * mm, 73 * mm, "Per-user operation files", green, colors.HexColor("#F4FBF6"))
    draw_card(pdf, 86 * mm, 132 * mm, 37 * mm, 14 * mm, "<id>.request.json", ["atomic write", "kind, sources, destination"], green, green_fill)
    draw_card(pdf, 86 * mm, 108 * mm, 37 * mm, 14 * mm, "<id>.state.json", ["atomic snapshots", "status, counts, item, HRESULT"], green, green_fill)
    draw_card(pdf, 86 * mm, 87 * mm, 37 * mm, 12 * mm, "<id>.cancel", ["cancellation sentinel"], amber, amber_fill)

    draw_container(pdf, 139 * mm, 82 * mm, 91 * mm, 73 * mm, "MultiExplorer.OperationHost.exe - one process per operation", green, colors.HexColor("#EEF9F2"))
    draw_card(pdf, 156 * mm, 132 * mm, 57 * mm, 14 * mm, "OperationHost", ["reads one request; initializes OLE"], green, colors.white)
    draw_card(pdf, 150 * mm, 99 * mm, 69 * mm, 22 * mm, "ShellFileOperation", ["Windows IFileOperation and progress sink", "check cancel at most every 75 ms", "publish progress at most every 150 ms"], green, green_fill)

    draw_card(pdf, 241 * mm, 107 * mm, 41 * mm, 28 * mm, "Windows Shell / file system", ["copy, move, recycle delete", "permanent delete"], BLUE, colors.HexColor("#EDF4FF"))

    draw_arrow(pdf, 65 * mm, 130 * mm, 86 * mm, 139 * mm, green, "1. write")
    draw_arrow(pdf, 123 * mm, 139 * mm, 156 * mm, 139 * mm, green, "2. launch --request")
    draw_arrow(pdf, 156 * mm, 132 * mm, 150 * mm, 121 * mm, green, "execute")
    draw_arrow(pdf, 150 * mm, 111 * mm, 123 * mm, 115 * mm, green, "3. progress / terminal state")
    draw_arrow(pdf, 86 * mm, 115 * mm, 65 * mm, 120 * mm, green, "4. poll")
    draw_arrow(pdf, 65 * mm, 115 * mm, 86 * mm, 93 * mm, amber, "cancel")
    draw_arrow(pdf, 123 * mm, 93 * mm, 150 * mm, 105 * mm, amber, "probe")
    draw_arrow(pdf, 219 * mm, 110 * mm, 241 * mm, 121 * mm, BLUE, "IFileOperation COM")

    pdf.setFillColor(MUTED)
    pdf.setFont("Helvetica", 7.2)
    pdf.drawString(17 * mm, 63 * mm, "Atomic replacement ensures readers see complete JSON documents. The worker writes the authoritative terminal state.")


def page_two(pdf: canvas.Canvas) -> None:
    pdf.setPageSize(A4)
    width, _ = A4
    draw_header(pdf, 2, "Explorer hosting and thread model", "One dedicated OLE STA and native Shell view per ExplorerHost")
    draw_explorer_hosting_overview(pdf)
    pdf.showPage()


def page_six(pdf: canvas.Canvas) -> None:
    width, height = landscape(A4)
    pdf.setPageSize((width, height))
    draw_header(pdf, 6, "File-operation IPC", "Durable file handoff between the UI and one isolated worker process")
    draw_file_operation_ipc_overview(pdf)
    pdf.showPage()


def add_table(pdf: canvas.Canvas, data: list[list[str]], widths: list[float], x: float, y_top: float, font_size: float = 7.6) -> float:
    head_style = TABLE_HEAD
    body_style = paragraph_style(font_size, font_size + 1.8)
    rows = []
    for row_index, row in enumerate(data):
        style = head_style if row_index == 0 else body_style
        rows.append([Paragraph(cell, style) for cell in row])
    table = Table(rows, colWidths=widths, repeatRows=1)
    table.setStyle(TableStyle([
        ("BACKGROUND", (0, 0), (-1, 0), NAVY),
        ("TEXTCOLOR", (0, 0), (-1, 0), colors.white),
        ("GRID", (0, 0), (-1, -1), 0.35, BORDER),
        ("VALIGN", (0, 0), (-1, -1), "TOP"),
        ("LEFTPADDING", (0, 0), (-1, -1), 6),
        ("RIGHTPADDING", (0, 0), (-1, -1), 6),
        ("TOPPADDING", (0, 0), (-1, -1), 5),
        ("BOTTOMPADDING", (0, 0), (-1, -1), 5),
        ("BACKGROUND", (0, 1), (-1, -1), colors.white),
        ("BACKGROUND", (0, 1), (0, -1), PALE),
    ]))
    _, height = table.wrap(sum(widths), y_top)
    table.drawOn(pdf, x, y_top - height)
    return y_top - height


def page_one(pdf: canvas.Canvas) -> None:
    width, height = landscape(A4)
    pdf.setPageSize((width, height))
    pdf.setFillColor(NAVY)
    pdf.setFont("Helvetica-Bold", 25)
    title_width = stringWidth(TITLE, "Helvetica-Bold", 25)
    pdf.drawString((width - title_width) / 2, height - 25 * mm, TITLE)
    pdf.setFillColor(MUTED)
    pdf.setFont("Helvetica", 10)
    pdf.drawString(16 * mm, height - 36 * mm, f"Runtime component view - v{VERSION}")
    draw_runtime_overview(pdf)
    draw_footer(pdf, 1, width)
    pdf.showPage()


def diagram_page(
    pdf: canvas.Canvas,
    page_number: int,
    heading: str,
    subtitle: str,
    image_path: Path,
    page_size: tuple[float, float] = A4,
) -> None:
    pdf.setPageSize(page_size)
    width, height = page_size
    y = draw_header(pdf, page_number, heading, subtitle)
    draw_image_fit(pdf, image_path, 16 * mm, y, width - 32 * mm, y - 21 * mm)
    pdf.showPage()


def page_three(pdf: canvas.Canvas) -> None:
    pdf.setPageSize(A4)
    width, _ = A4
    y = draw_header(pdf, 3, "Runtime composition and Explorer hosting", "Process boundaries, UI ownership, and Shell thread affinity")
    x = 16 * mm
    content_width = width - 32 * mm
    y = draw_section(pdf, "1. Application bootstrap and lifetime", "<b>Program</b> acquires a named single-instance mutex before any UI exists. A later interactive launch broadcasts a registered Windows message so the existing application restores and activates itself; duplicate Windows-startup launches remain silent. Settings and the selected application theme load before WinForms creates a window, then the process enables PerMonitorV2 DPI awareness and creates <b>MainForm</b> on the UI STA.", x, y, content_width) - 5 * mm
    y = draw_section(pdf, "2. Dual-pane ownership", "<b>MainForm</b> owns the two independent <b>PanelView</b> controls, custom splitter, tray icon, status bar, global hotkey, persistence, and file-operation exit policy. A panel owns its tabs, breadcrumb, command bar, filter overlay, details pane, preview pane, and one <b>ExplorerHost</b> per tab. Only the active host is visible. The Open in other pane action passes the current folder to the opposite panel; it does not copy anything.", x, y, content_width) - 5 * mm
    y = draw_section(pdf, "3. Dedicated Shell STA", "Each ExplorerHost owns a BrowserThread with an OLE-initialized STA and message pump. IExplorerBrowser and its native Shell child window live there, not on the WinForms UI thread. Invoke marshals COM work when a result is needed; Post performs non-blocking work such as bounds changes. This isolates Shell modal loops during drag/drop or a Shell operation and preserves responsiveness of the managed UI.", x, y, content_width) - 5 * mm
    draw_section(pdf, "4. Native functionality", "ExplorerHost routes Shell accelerators, context menus, drag/drop, view options, navigation-pane synchronization, selection changes, and QuickLook requests. Navigation callbacks refresh cached paths so the UI does not need repeated synchronous cross-thread COM reads. DetailsPanel uses Shell icons and metadata; PreviewPane discovers a registered IPreviewHandler and hosts it in a child window.", x, y, content_width)
    pdf.showPage()


def page_five(pdf: canvas.Canvas) -> None:
    pdf.setPageSize(A4)
    width, _ = A4
    y = draw_header(pdf, 5, "Input, navigation, filtering, and selection", "Managed behavior layered over the active native Shell view")
    x = 16 * mm
    content_width = width - 32 * mm
    y = draw_section(pdf, "1. Input ownership", "BrowserThread's MessageHook classifies input before normal message dispatch. Application commands are raised to the managed command layer. Space can target a running QuickLook instance only when that option is enabled. Other printable characters begin the contains filter, while standard Explorer keys are forwarded through IShellView::TranslateAccelerator.", x, y, content_width) - 5 * mm
    y = draw_section(pdf, "2. Type-to-filter", "PanelView waits 180 ms after the latest character, cancels earlier work, and asynchronously enumerates the current directory. Folder matches precede file matches and both are alphabetical. The FilterResultsListView overlay is double-buffered and owner-drawn, so hover feedback redraws only the affected rows instead of repainting the full list.", x, y, content_width) - 5 * mm
    y = draw_section(pdf, "3. Preserving selection", "Opening a matched folder navigates and clears the overlay. Opening a matched file first selects that path in the underlying ExplorerHost, then clears the overlay and invokes the default file handler. Navigation callbacks update cached path information, and MainForm polls paths every 300 ms so tab labels, path bars, persistence, and optional selection panes remain current.", x, y, content_width) - 5 * mm
    draw_section(pdf, "4. Stable presentation", "The result list overlays the native Shell content but the yellow filter bar is kept outside the host container. Native child windows therefore cannot paint over the text entry controls. Hover handling invalidates only the old and new rows, preserving the expected Explorer-style rollover without flickering the entire filtered list.", x, y, content_width)
    pdf.showPage()


def page_seven(pdf: canvas.Canvas) -> None:
    pdf.setPageSize(A4)
    width, _ = A4
    y = draw_header(pdf, 7, "Isolated file operations and IPC", "One worker process per copy, move, recycle-delete, or permanent-delete request")
    x = 16 * mm
    content_width = width - 32 * mm
    y = draw_section(pdf, "1. Durable process handoff", "OperationManager validates the requested paths, allocates a GUID, atomically writes a request JSON file, starts MultiExplorer.OperationHost.exe with that request path, and records a queued state. The worker initializes OLE, creates Windows IFileOperation, and reports its own process ID and progress through atomic state snapshots.", x, y, content_width) - 5 * mm
    y = draw_section(pdf, "2. Progress, cancellation, and failure detection", "The worker's progress sink publishes state no more often than every 150 ms except for terminal or failure updates. OperationManager polls every 500 ms, updates the status and tray UI, and marks an operation failed if a worker disappears without the required terminal state. Cancellation creates an id-specific sentinel file; a worker checks it at most every 75 ms and asks IFileOperation to abort safely.", x, y, content_width) - 5 * mm
    y = draw_section(pdf, "3. Shell fidelity and containment", "The worker delegates copy, move, recycle delete, and permanent delete to native IFileOperation. This retains Windows conflict handling and file-operation semantics while preventing worker lifetime or a native modal loop from taking down the interactive explorer window.", x, y, content_width) - 5 * mm
    draw_section(pdf, "4. Terminal states", "The shared contract distinguishes Queued, Running, Cancelling, Completed, Failed, and Cancelled states. The terminal state is the authoritative worker outcome and includes any Shell HRESULT and message needed for diagnostics. The file-based exchange permits the UI and worker processes to be restarted or observed independently.", x, y, content_width)
    pdf.showPage()


def page_eight(pdf: canvas.Canvas) -> None:
    pdf.setPageSize(A4)
    width, _ = A4
    y = draw_header(pdf, 8, "Persistence, integrations, and design decisions", "Local per-user state and feature-detected Windows integrations")
    x = 16 * mm
    content_width = width - 32 * mm
    integrations = [
        ["Integration", "Purpose"],
        ["%APPDATA%\\MultiExplorer\\settings.json", "Persisted window, panel, tab, path history, tray, theme, hotkey, startup, and QuickLook state."],
        ["%LOCALAPPDATA%\\MultiExplorer\\app.log", "Rolling diagnostic log. Warnings and errors also reach the status bar."],
        ["%LOCALAPPDATA%\\MultiExplorer\\Operations", "Atomic request, state, and cancellation files shared with operation-host processes."],
        ["Current-user registry", "Windows sign-in Run registration and Explorer display preferences that intentionally follow File Explorer."],
        ["Classes-root registry", "Preview-handler discovery by file extension or ProgID."],
        ["Windows Shell COM", "Explorer views, thumbnails, context menus, drag/drop, preview handlers, and file operations."],
        ["QuickLook named pipe", "Optional outbound message carrying the selected path to a running QuickLook process."],
    ]
    y = add_table(pdf, integrations, [59 * mm, content_width - 59 * mm], x, y, 7.8) - 7 * mm
    concerns = [
        ["Concern", "Implementation consequence"],
        ["Responsiveness", "The UI STA coordinates managed controls; each native Explorer view has a dedicated STA. Filtering is asynchronous, debounced, and cancellable."],
        ["Fault containment", "Shell file operations run in a separate executable. Stale state and an unexpectedly exited worker become an explicit failure."],
        ["Windows fidelity", "Navigation, icons, menus, drag/drop, previews, and file operations intentionally delegate to Windows Shell interfaces and installed extensions."],
        ["Theme and DPI", "ThemeManager combines a managed palette with native theme treatment. Shell views are recreated after live theme changes; PerMonitorV2 keeps host and child rendering aligned."],
        ["Optional dependency", "QuickLook is feature-detected. A missing process or pipe produces a targeted message without affecting the core explorer."],
    ]
    pdf.setFillColor(BLUE)
    pdf.setFont("Helvetica-Bold", 13)
    pdf.drawString(x, y, "Architecture considerations")
    add_table(pdf, concerns, [43 * mm, content_width - 43 * mm], x, y - 6 * mm, 7.4)
    pdf.showPage()


def page_nine(pdf: canvas.Canvas) -> None:
    pdf.setPageSize(A4)
    width, _ = A4
    y = draw_header(pdf, 9, "Component reference and maintenance", "Source-level responsibility map")
    x = 16 * mm
    content_width = width - 32 * mm
    components = [
        ["Component", "Source", "Runtime responsibility"],
        ["Program", "Program.cs", "Bootstrap, single instance, settings/theme loading, DPI mode, and message loop."],
        ["MainForm", "MainForm.cs", "Top-level window, dual panes, splitter, tray, hotkey, status, exit policy, and operation coordination."],
        ["PanelView", "PanelView.cs", "Per-pane composition, tab ownership, filtering, optional panes, and command dispatch."],
        ["ExplorerHost / BrowserThread", "ExplorerHost.cs / BrowserThread.cs", "Per-tab native Shell hosting, input routing, navigation, selection, and dedicated STA/OLE message pump."],
        ["PathBar / TabBar / CommandBar", "PathBar.cs / TabBar.cs / CommandBar.cs", "Managed navigation, tab UI, Shell/application commands, and custom layered popups."],
        ["DetailsPanel / PreviewPane", "DetailsPanel.cs / PreviewPane.cs", "Metadata and preview-handler hosting for the selected item."],
        ["OperationManager / contracts", "OperationManager.cs / OperationContracts.cs", "Worker launch, state polling, cancellation, JSON models, and atomic store."],
        ["OperationHost / ShellFileOperation", "MultiExplorer.OperationHost/", "Per-operation OLE worker and IFileOperation execution."],
        ["ThemeManager", "ThemeManager.cs", "Palette, managed menu renderer, and native dark-mode application."],
        ["SettingsManager / StartupManager / AppLog", "SettingsManager.cs / StartupManager.cs / AppLog.cs", "Per-user persistence, Run-key registration, and rolling diagnostic logging."],
        ["NativeMethods", "NativeMethods.cs", "P/Invoke and COM interop declarations."],
    ]
    y = add_table(pdf, components, [43 * mm, 43 * mm, content_width - 86 * mm], x, y, 7.1) - 8 * mm
    draw_section(pdf, "Maintenance rule", "Update this document, the Markdown reference, and the four Graphviz DOT diagrams whenever a process boundary, IPC contract, storage location, Windows Shell integration point, thread ownership rule, or major UI responsibility changes. Routine visual changes normally require only a note update unless they alter one of these boundaries.", x, y, content_width)
    pdf.showPage()


def main() -> None:
    required_images = [
        IMAGES / "application-architecture.png",
        IMAGES / "explorer-hosting.png",
        IMAGES / "filter-and-input-flow.png",
        IMAGES / "file-operation-ipc.png",
    ]
    missing = [str(path) for path in required_images if not path.exists()]
    if missing:
        raise FileNotFoundError("Render the Graphviz diagrams first: " + ", ".join(missing))

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    pdf = canvas.Canvas(str(OUTPUT), pagesize=landscape(A4), pageCompression=1)
    pdf.setTitle(TITLE)
    pdf.setAuthor("Architecture documentation generated from the MultiExplorer source repository")
    pdf.setSubject("Runtime application architecture diagrams and notes")
    pdf.setKeywords("MultiExplorer, architecture, Windows Shell, WinForms, IExplorerBrowser")
    page_one(pdf)
    page_two(pdf)
    page_three(pdf)
    diagram_page(pdf, 4, "Input and type-to-filter flow", "Managed filtering and application shortcuts layered over the active Shell view", IMAGES / "filter-and-input-flow.png")
    page_five(pdf)
    page_six(pdf)
    page_seven(pdf)
    page_eight(pdf)
    page_nine(pdf)
    pdf.save()


if __name__ == "__main__":
    main()
