// Weva Export — Figma plugin (main thread).
//
// Serializes the selected frame's subtree to the JSON shape the C# bridge parses
// (Weva.Figma.Model.FigmaNode), plus local variables (the meta.variables /
// meta.variableCollections shape VariablesToCss consumes) and rasterized images
// (base64 PNG) for image fills and vector shapes. This is the tokenless path: no
// Figma REST token needed. The companion Unity menu imports the result.
//
// Plugin-API field names/values are translated to the REST forms the C# model
// expects (notably constraint values MIN/MAX/STRETCH -> LEFT/RIGHT/LEFT_RIGHT),
// and image file names mirror C# RasterNaming so the generated CSS resolves.

figma.showUI(__html__, { width: 380, height: 520 });

figma.ui.onmessage = async (msg: any) => {
  if (msg.type === "export") {
    try {
      const payload = await buildExport();
      figma.ui.postMessage({ type: "export-result", payload });
    } catch (e: any) {
      figma.ui.postMessage({ type: "error", message: String(e && e.message ? e.message : e) });
    }
  } else if (msg.type === "close") {
    figma.closePlugin();
  }
};

async function buildExport(): Promise<any> {
  const warnings: string[] = [];
  const images: { [k: string]: string } = {};

  const sel = figma.currentPage.selection;
  let root: SceneNode | null = sel.length > 0 ? sel[0] : null;
  if (!root) {
    for (const n of figma.currentPage.children) {
      if (isExportable(n)) { root = n; break; }
    }
  }
  if (!root) {
    return { name: "export", node: null, variables: null, images: {}, warnings: ["Select a frame or component to export."] };
  }

  const node = await serializeNode(root, images, warnings);
  const variables = await serializeVariables();
  return { name: sanitize(root.name) || "export", node, variables, images, warnings };
}

function isExportable(n: BaseNode): boolean {
  return n.type === "FRAME" || n.type === "COMPONENT" || n.type === "COMPONENT_SET"
    || n.type === "INSTANCE" || n.type === "SECTION";
}

function isVectorType(t: string): boolean {
  return t === "VECTOR" || t === "BOOLEAN_OPERATION" || t === "STAR" || t === "REGULAR_POLYGON" || t === "LINE";
}

async function serializeNode(node: any, images: { [k: string]: string }, warnings: string[]): Promise<any> {
  const o: any = { id: node.id, name: node.name, type: node.type, visible: node.visible !== false };

  if (node.absoluteBoundingBox) {
    const b = node.absoluteBoundingBox;
    o.absoluteBoundingBox = { x: b.x, y: b.y, width: b.width, height: b.height };
  }

  if ("layoutMode" in node) {
    o.layoutMode = node.layoutMode;
    if (node.layoutMode !== "NONE") {
      o.primaryAxisAlignItems = node.primaryAxisAlignItems;
      o.counterAxisAlignItems = node.counterAxisAlignItems;
      o.layoutWrap = node.layoutWrap;
      o.itemSpacing = node.itemSpacing;
      o.paddingLeft = node.paddingLeft;
      o.paddingRight = node.paddingRight;
      o.paddingTop = node.paddingTop;
      o.paddingBottom = node.paddingBottom;
    }
    o.clipsContent = node.clipsContent;
  }

  if ("layoutSizingHorizontal" in node) o.layoutSizingHorizontal = node.layoutSizingHorizontal;
  if ("layoutSizingVertical" in node) o.layoutSizingVertical = node.layoutSizingVertical;
  if ("layoutAlign" in node) o.layoutAlign = node.layoutAlign;
  if ("layoutGrow" in node) o.layoutGrow = node.layoutGrow;
  if ("layoutPositioning" in node) o.layoutPositioning = node.layoutPositioning;

  if (node.constraints) {
    o.constraints = { horizontal: hConstraint(node.constraints.horizontal), vertical: vConstraint(node.constraints.vertical) };
  }

  if ("opacity" in node) o.opacity = node.opacity;
  if ("blendMode" in node) o.blendMode = node.blendMode;

  if (typeof node.cornerRadius === "number") o.cornerRadius = node.cornerRadius;
  if ("topLeftRadius" in node) {
    o.rectangleCornerRadii = [node.topLeftRadius, node.topRightRadius, node.bottomRightRadius, node.bottomLeftRadius];
  }

  if (Array.isArray(node.fills)) o.fills = node.fills.map(serializePaint);
  if (Array.isArray(node.strokes)) o.strokes = node.strokes.map(serializePaint);
  if (typeof node.strokeWeight === "number") o.strokeWeight = node.strokeWeight;
  if ("strokeAlign" in node) o.strokeAlign = node.strokeAlign;
  if (Array.isArray(node.effects)) o.effects = node.effects.map(serializeEffect);

  if (node.type === "TEXT") {
    o.characters = node.characters;
    o.style = serializeTextStyle(node, warnings);
  }

  if (isVectorType(node.type)) {
    await exportNodeImage(node, vectorFile(node), images, warnings);
    return o; // a rasterized shape absorbs its children
  }

  if (Array.isArray(node.fills)) {
    for (const p of node.fills) {
      if (p && p.type === "IMAGE" && p.visible !== false && p.imageHash) {
        await exportImageFill(p.imageHash, images, warnings);
      }
    }
  }

  if (Array.isArray(node.children)) {
    o.children = [];
    for (const child of node.children) o.children.push(await serializeNode(child, images, warnings));
  }

  return o;
}

function serializePaint(p: any): any {
  const o: any = { type: p.type, visible: p.visible !== false, opacity: typeof p.opacity === "number" ? p.opacity : 1 };
  if (p.type === "SOLID") o.color = { r: p.color.r, g: p.color.g, b: p.color.b, a: 1 };
  if (typeof p.type === "string" && p.type.indexOf("GRADIENT_") === 0) {
    o.gradientStops = (p.gradientStops || []).map((s: any) => ({
      position: s.position, color: { r: s.color.r, g: s.color.g, b: s.color.b, a: s.color.a }
    }));
    // The plugin API exposes a gradientTransform, not handle positions; default
    // to top->bottom. The REST import path produces precise angles.
    o.gradientHandlePositions = [{ x: 0, y: 0 }, { x: 0, y: 1 }];
  }
  if (p.type === "IMAGE") { o.imageRef = p.imageHash; o.scaleMode = p.scaleMode; }
  return o;
}

function serializeEffect(e: any): any {
  const o: any = { type: e.type, visible: e.visible !== false };
  if ("radius" in e) o.radius = e.radius;
  o.spread = typeof e.spread === "number" ? e.spread : 0;
  if (e.color) o.color = { r: e.color.r, g: e.color.g, b: e.color.b, a: e.color.a };
  if (e.offset) o.offset = { x: e.offset.x, y: e.offset.y };
  return o;
}

function serializeTextStyle(t: any, warnings: string[]): any {
  const o: any = {};
  if (t.fontName !== figma.mixed) {
    o.fontFamily = t.fontName.family;
    o.italic = String(t.fontName.style).toLowerCase().indexOf("italic") >= 0;
    o.fontWeight = weightFromStyle(t.fontName.style);
  } else {
    warnings.push('Text "' + t.name + '" has mixed fonts; exporting the base style only.');
  }
  if (t.fontSize !== figma.mixed) o.fontSize = t.fontSize;
  o.textAlignHorizontal = t.textAlignHorizontal;
  if (t.textCase !== figma.mixed) o.textCase = t.textCase;
  if (t.textDecoration !== figma.mixed) o.textDecoration = t.textDecoration;
  if (t.letterSpacing !== figma.mixed && t.letterSpacing.unit === "PIXELS") o.letterSpacing = t.letterSpacing.value;
  if (t.lineHeight !== figma.mixed) {
    if (t.lineHeight.unit === "PIXELS") { o.lineHeightPx = t.lineHeight.value; o.lineHeightUnit = "PIXELS"; }
    else if (t.lineHeight.unit === "PERCENT") { o.lineHeightPercentFontSize = t.lineHeight.value; o.lineHeightUnit = "FONT_SIZE_%"; }
  }
  return o;
}

async function serializeVariables(): Promise<any> {
  try {
    const collections = await figma.variables.getLocalVariableCollectionsAsync();
    const vars = await figma.variables.getLocalVariablesAsync();
    if (collections.length === 0 && vars.length === 0) return null;

    const variableCollections: any = {};
    for (const c of collections) {
      variableCollections[c.id] = {
        id: c.id, name: c.name, defaultModeId: c.defaultModeId,
        modes: c.modes.map((m) => ({ modeId: m.modeId, name: m.name })),
        variableIds: c.variableIds,
      };
    }
    const variables: any = {};
    for (const v of vars) {
      variables[v.id] = {
        id: v.id, name: v.name, resolvedType: v.resolvedType,
        variableCollectionId: v.variableCollectionId, scopes: v.scopes,
        valuesByMode: v.valuesByMode,
      };
    }
    return { meta: { variableCollections, variables } };
  } catch (e) {
    return null;
  }
}

async function exportNodeImage(node: any, filename: string, images: { [k: string]: string }, warnings: string[]) {
  try {
    const bytes = await node.exportAsync({ format: "PNG", constraint: { type: "SCALE", value: 2 } });
    images[filename] = figma.base64Encode(bytes);
  } catch (e) {
    warnings.push('Could not rasterize "' + node.name + '".');
  }
}

async function exportImageFill(imageHash: string, images: { [k: string]: string }, warnings: string[]) {
  try {
    const img = figma.getImageByHash(imageHash);
    if (!img) return;
    const bytes = await img.getBytesAsync();
    images["images/" + sanitize(imageHash) + ".png"] = figma.base64Encode(bytes);
  } catch (e) {
    warnings.push("Could not export an image fill.");
  }
}

// --- value translation (must mirror the C# side) ---

function hConstraint(c: string): string {
  switch (c) {
    case "MIN": return "LEFT";
    case "MAX": return "RIGHT";
    case "CENTER": return "CENTER";
    case "STRETCH": return "LEFT_RIGHT";
    case "SCALE": return "SCALE";
    default: return "LEFT";
  }
}

function vConstraint(c: string): string {
  switch (c) {
    case "MIN": return "TOP";
    case "MAX": return "BOTTOM";
    case "CENTER": return "CENTER";
    case "STRETCH": return "TOP_BOTTOM";
    case "SCALE": return "SCALE";
    default: return "TOP";
  }
}

function weightFromStyle(style: string): number {
  const s = String(style).toLowerCase();
  if (s.indexOf("thin") >= 0) return 100;
  if (s.indexOf("extralight") >= 0 || s.indexOf("ultralight") >= 0) return 200;
  if (s.indexOf("semibold") >= 0 || s.indexOf("demibold") >= 0) return 600;
  if (s.indexOf("extrabold") >= 0 || s.indexOf("ultrabold") >= 0) return 800;
  if (s.indexOf("black") >= 0 || s.indexOf("heavy") >= 0) return 900;
  if (s.indexOf("light") >= 0) return 300;
  if (s.indexOf("medium") >= 0) return 500;
  if (s.indexOf("bold") >= 0) return 700;
  return 400;
}

// Mirrors Weva.Figma.CssText.SanitizeIdent + RasterNaming.
function sanitize(s: string): string {
  if (!s) return "";
  let out = "";
  let lastDash = false;
  const lower = s.toLowerCase();
  for (let i = 0; i < lower.length; i++) {
    const ch = lower[i];
    const ok = (ch >= "a" && ch <= "z") || (ch >= "0" && ch <= "9");
    if (ok) { out += ch; lastDash = false; }
    else if (!lastDash && out.length > 0) { out += "-"; lastDash = true; }
  }
  while (out.length > 0 && out[out.length - 1] === "-") out = out.slice(0, -1);
  return out;
}

function vectorFile(node: any): string {
  return "images/" + (sanitize(node.name) || "vector") + "-" + (sanitize(node.id) || "0") + ".png";
}
