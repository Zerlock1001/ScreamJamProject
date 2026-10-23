(() => {
  "use strict";

  const APP_FORMAT = "huabu-studio";
  const APP_VERSION = 1;
  const DEFAULT_WIDTH = 1200;
  const DEFAULT_HEIGHT = 900;
  const MIN_LAYER_SIZE = 12;
  const DEFAULT_TEXT_FONT = '"Microsoft YaHei", "PingFang SC", sans-serif';
  const DEFAULT_TEXT_COLOR = "#1d1d1f";
  const SYNC_CHANNEL = "huabu-studio-sync";
  const DB_NAME = "huabu-studio-db";
  const DB_STORE = "kv";
  const STORE_KEY = "project";
  const LS_PROJECT = "huabu-studio-project-v1";
  const LS_META = "huabu-studio-meta-v1";

  const clientId = (() => {
    try {
      if (window.crypto && typeof window.crypto.randomUUID === "function") {
        return window.crypto.randomUUID();
      }
    } catch (_) {
      /* ignore */
    }
    return `client_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
  })();

  const state = {
    project: null,
    currentPageId: null,
    selectedLayerId: null,
    renamingPageId: null,
    zoom: 1,
    zoomMode: "manual",
    revision: 0,
    dirty: false,
    interaction: null,
    undoStack: [],
    redoStack: [],
    dragLayerId: null,
    dropTarget: null,
    previewIndex: 0,
    storageMode: "indexeddb",
    editingTextLayerId: null,
    textEditBefore: null,
    textEditCancel: false,
    initialized: false
  };

  const imageCache = new Map();
  let saveTimer = null;
  let savePromise = null;
  let saveAgain = false;
  let pollTimer = null;
  let remoteChecking = false;
  let syncChannel = null;
  let dragFileDepth = 0;

  const $ = (id) => document.getElementById(id);

  const el = {
    saveState: $("saveState"),
    saveText: $("saveText"),
    undoBtn: $("undoBtn"),
    redoBtn: $("redoBtn"),
    importBtn: $("importBtn"),
    exportBtn: $("exportBtn"),
    helpBtn: $("helpBtn"),
    previewBtn: $("previewBtn"),
    addTextBtn: $("addTextBtn"),
    textInspector: $("textInspector"),
    textFontSelect: $("textFontSelect"),
    fontSizeMinus: $("fontSizeMinus"),
    fontSizePlus: $("fontSizePlus"),
    fontSizeInput: $("fontSizeInput"),
    textBoldBtn: $("textBoldBtn"),
    textColorInput: $("textColorInput"),
    textColorDot: $("textColorDot"),
    addPageBtn: $("addPageBtn"),
    pageCount: $("pageCount"),
    pagesList: $("pagesList"),
    uploadBtn: $("uploadBtn"),
    emptyUploadBtn: $("emptyUploadBtn"),
    assetInput: $("assetInput"),
    importInput: $("importInput"),
    assetDropZone: $("assetDropZone"),
    assetsGrid: $("assetsGrid"),
    assetEmpty: $("assetEmpty"),
    assetCount: $("assetCount"),
    workspace: $("workspace"),
    workspaceScroll: $("workspaceScroll"),
    stage: $("stage"),
    canvasScale: $("canvasScale"),
    canvasFrame: $("canvasFrame"),
    canvas: $("canvas"),
    selectionOverlay: $("selectionOverlay"),
    selectionBox: $("selectionBox"),
    selBadge: $("selBadge"),
    layerCount: $("layerCount"),
    layersList: $("layersList"),
    layersEmpty: $("layersEmpty"),
    zoomOutBtn: $("zoomOutBtn"),
    zoomInBtn: $("zoomInBtn"),
    zoomValueBtn: $("zoomValueBtn"),
    fitBtn: $("fitBtn"),
    previewModal: $("previewModal"),
    previewTitle: $("previewTitle"),
    previewCloseBtn: $("previewCloseBtn"),
    previewPrevBtn: $("previewPrevBtn"),
    previewNextBtn: $("previewNextBtn"),
    previewPngBtn: $("previewPngBtn"),
    previewStageWrap: $("previewStageWrap"),
    previewHolder: $("previewHolder"),
    previewCanvas: $("previewCanvas"),
    previewCounter: $("previewCounter"),
    helpModal: $("helpModal"),
    helpCloseBtn: $("helpCloseBtn"),
    helpDoneBtn: $("helpDoneBtn"),
    storageModeText: $("storageModeText"),
    toastStack: $("toastStack")
  };

  // ---------------------------------------------------------------------------
  // Small utilities
  // ---------------------------------------------------------------------------

  function uid(prefix) {
    const rand = Math.random().toString(36).slice(2, 9);
    return `${prefix}_${Date.now().toString(36)}_${rand}`;
  }

  function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
  }

  function normalizeId(value, prefix) {
    return typeof value === "string" && /^[A-Za-z0-9_-]{1,120}$/.test(value)
      ? value
      : uid(prefix);
  }

  function num(value, fallback = 0) {
    const result = Number(value);
    return Number.isFinite(result) ? result : fallback;
  }

  function deepClone(value) {
    return JSON.parse(JSON.stringify(value));
  }

  function stripExtension(name) {
    return String(name || "图片").replace(/\.[^.]+$/, "").trim() || "图片";
  }

  function sanitizeFilename(name) {
    return String(name || "image").replace(/[\\/:*?"<>|]/g, "_").replace(/\s+/g, " ").trim();
  }

  function normalizeColor(value) {
    return typeof value === "string" && /^#[0-9a-f]{6}$/i.test(value) ? value : DEFAULT_TEXT_COLOR;
  }

  function formatTime(timestamp) {
    const date = new Date(timestamp || Date.now());
    return `${String(date.getHours()).padStart(2, "0")}:${String(date.getMinutes()).padStart(2, "0")}`;
  }

  function dateStamp() {
    const date = new Date();
    return [
      date.getFullYear(),
      String(date.getMonth() + 1).padStart(2, "0"),
      String(date.getDate()).padStart(2, "0")
    ].join("");
  }

  function icon(name, className = "ico") {
    return `<svg class="${className}" aria-hidden="true"><use href="#i-${name}"></use></svg>`;
  }

  function isTyping() {
    if (state.editingTextLayerId) return true;
    const active = document.activeElement;
    if (!active) return false;
    const tag = active.tagName;
    return tag === "INPUT" || tag === "TEXTAREA" || active.isContentEditable;
  }

  function readFileAsDataURL(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result || ""));
      reader.onerror = () => reject(reader.error || new Error("文件读取失败"));
      reader.readAsDataURL(file);
    });
  }

  function loadImage(src) {
    if (imageCache.has(src)) return imageCache.get(src);
    const promise = new Promise((resolve, reject) => {
      const image = new Image();
      image.decoding = "async";
      image.onload = () => resolve(image);
      image.onerror = () => reject(new Error("图片解码失败"));
      image.src = src;
    });
    imageCache.set(src, promise);
    promise.catch(() => imageCache.delete(src));
    return promise;
  }

  function downloadBlob(blob, filename) {
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = filename;
    anchor.style.display = "none";
    document.body.append(anchor);
    anchor.click();
    anchor.remove();
    window.setTimeout(() => URL.revokeObjectURL(url), 1500);
  }

  // ---------------------------------------------------------------------------
  // Storage: local server file first, then IndexedDB / localStorage fallback
  // ---------------------------------------------------------------------------

  const Store = (() => {
    let mode = "indexeddb";
    let db = null;

    function openDatabase() {
      return new Promise((resolve, reject) => {
        if (!window.indexedDB) {
          reject(new Error("IndexedDB 不可用"));
          return;
        }
        const request = indexedDB.open(DB_NAME, 1);
        request.onupgradeneeded = () => {
          const database = request.result;
          if (!database.objectStoreNames.contains(DB_STORE)) {
            database.createObjectStore(DB_STORE);
          }
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error || new Error("IndexedDB 打开失败"));
      });
    }

    function idbGet(key) {
      return new Promise((resolve, reject) => {
        const transaction = db.transaction(DB_STORE, "readonly");
        const request = transaction.objectStore(DB_STORE).get(key);
        request.onsuccess = () => resolve(request.result || null);
        request.onerror = () => reject(request.error || new Error("读取失败"));
      });
    }

    function idbPut(key, value) {
      return new Promise((resolve, reject) => {
        const transaction = db.transaction(DB_STORE, "readwrite");
        transaction.objectStore(DB_STORE).put(value, key);
        transaction.oncomplete = () => resolve();
        transaction.onerror = () => reject(transaction.error || new Error("写入失败"));
        transaction.onabort = () => reject(transaction.error || new Error("写入中止"));
      });
    }

    function writeMeta(revision, updatedAt) {
      try {
        localStorage.setItem(LS_META, JSON.stringify({ revision, updatedAt, clientId }));
      } catch (_) {
        /* localStorage might be unavailable */
      }
    }

    function readLocalMeta() {
      try {
        const raw = localStorage.getItem(LS_META);
        if (!raw) return null;
        const parsed = JSON.parse(raw);
        return Number.isFinite(parsed.revision) ? parsed : null;
      } catch (_) {
        return null;
      }
    }

    async function apiJson(path, options) {
      const response = await fetch(path, {
        cache: "no-store",
        ...options
      });
      if (!response.ok) {
        throw new Error(`本地服务返回 ${response.status}`);
      }
      return response.json();
    }

    async function init() {
      if (location.protocol === "http:" || location.protocol === "https:") {
        try {
          const meta = await apiJson("/api/meta");
          if (meta && Number.isFinite(meta.revision)) {
            mode = "server";
            return mode;
          }
        } catch (_) {
          /* not running through server.js */
        }
      }

      try {
        db = await openDatabase();
        mode = "indexeddb";
        return mode;
      } catch (_) {
        mode = "localstorage";
        return mode;
      }
    }

    async function load() {
      if (mode === "server") {
        const data = await apiJson("/api/project");
        return {
          project: data.project || null,
          revision: num(data.revision, 0),
          updatedAt: num(data.updatedAt, 0)
        };
      }

      if (mode === "indexeddb") {
        const record = await idbGet(STORE_KEY);
        return {
          project: record ? record.project : null,
          revision: record ? num(record.revision, 0) : 0,
          updatedAt: record ? num(record.updatedAt, 0) : 0
        };
      }

      try {
        const raw = localStorage.getItem(LS_PROJECT);
        if (!raw) return { project: null, revision: 0, updatedAt: 0 };
        const record = JSON.parse(raw);
        return {
          project: record.project || null,
          revision: num(record.revision, 0),
          updatedAt: num(record.updatedAt, 0)
        };
      } catch (_) {
        return { project: null, revision: 0, updatedAt: 0 };
      }
    }

    async function save(project) {
      if (mode === "server") {
        const data = await apiJson("/api/project", {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ project })
        });
        project.updatedAt = num(data.updatedAt, Date.now());
        return { revision: num(data.revision, 0), updatedAt: project.updatedAt };
      }

      if (mode === "indexeddb") {
        const existing = await idbGet(STORE_KEY);
        const revision = num(existing && existing.revision, 0) + 1;
        const updatedAt = Date.now();
        project.updatedAt = updatedAt;
        await idbPut(STORE_KEY, { project, revision, updatedAt });
        writeMeta(revision, updatedAt);
        return { revision, updatedAt };
      }

      const existing = await load();
      const revision = existing.revision + 1;
      const updatedAt = Date.now();
      project.updatedAt = updatedAt;
      localStorage.setItem(LS_PROJECT, JSON.stringify({ project, revision, updatedAt }));
      writeMeta(revision, updatedAt);
      return { revision, updatedAt };
    }

    async function meta() {
      if (mode === "server") {
        return apiJson("/api/meta");
      }

      const local = readLocalMeta();
      if (local) return local;

      if (mode === "indexeddb") {
        const record = await idbGet(STORE_KEY);
        return {
          revision: record ? num(record.revision, 0) : 0,
          updatedAt: record ? num(record.updatedAt, 0) : 0
        };
      }

      const record = await load();
      return { revision: record.revision, updatedAt: record.updatedAt };
    }

    return {
      init,
      load,
      save,
      meta,
      get mode() {
        return mode;
      },
      get metaKey() {
        return LS_META;
      }
    };
  })();

  // ---------------------------------------------------------------------------
  // Project model
  // ---------------------------------------------------------------------------

  function createPage(name, width = DEFAULT_WIDTH, height = DEFAULT_HEIGHT) {
    return {
      id: uid("page"),
      name: name || "页面",
      width,
      height,
      layers: []
    };
  }

  function createDefaultProject() {
    const page = createPage("页面 1");
    return {
      format: APP_FORMAT,
      version: APP_VERSION,
      updatedAt: Date.now(),
      pages: [page],
      assets: []
    };
  }

  function normalizeProject(raw) {
    if (!raw || typeof raw !== "object") throw new Error("项目内容为空");
    if (!Array.isArray(raw.pages) || raw.pages.length === 0) throw new Error("项目中没有任何页面");

    const assets = [];
    const assetIds = new Set();

    if (Array.isArray(raw.assets)) {
      for (const candidate of raw.assets) {
        if (!candidate || typeof candidate.dataUrl !== "string") continue;
        if (!candidate.dataUrl.startsWith("data:image/")) continue;

        let id = normalizeId(candidate.id, "asset");
        if (assetIds.has(id)) id = uid("asset");
        assetIds.add(id);

        assets.push({
          id,
          name: String(candidate.name || "图片").slice(0, 160),
          dataUrl: candidate.dataUrl,
          width: clamp(num(candidate.width, 100), 1, 20000),
          height: clamp(num(candidate.height, 100), 1, 20000),
          type: String(candidate.type || "image/png"),
          size: Math.max(0, num(candidate.size, 0)),
          createdAt: num(candidate.createdAt, Date.now())
        });
      }
    }

    const usedLayerIds = new Set();
    const pages = raw.pages.map((candidate, pageIndex) => {
      const width = clamp(num(candidate.width, DEFAULT_WIDTH), 200, 5000);
      const height = clamp(num(candidate.height, DEFAULT_HEIGHT), 200, 5000);
      const layers = [];

      if (Array.isArray(candidate.layers)) {
        for (const layerCandidate of candidate.layers) {
          if (!layerCandidate) continue;
          const type = layerCandidate.type === "text" ? "text" : "image";
          const assetId = String(layerCandidate.assetId || "");
          if (type === "image" && !assetIds.has(assetId)) continue;

          let id = normalizeId(layerCandidate.id, "layer");
          if (usedLayerIds.has(id)) id = uid("layer");
          usedLayerIds.add(id);

          const base = {
            id,
            type,
            name: String(layerCandidate.name || (type === "text" ? "文字" : "图片")).slice(0, 160),
            x: num(layerCandidate.x, 0),
            y: num(layerCandidate.y, 0),
            w: Math.max(1, num(layerCandidate.w, type === "text" ? 480 : 200)),
            h: Math.max(1, num(layerCandidate.h, type === "text" ? 96 : 200)),
            rotation: num(layerCandidate.rotation, 0),
            hidden: Boolean(layerCandidate.hidden),
            locked: Boolean(layerCandidate.locked)
          };

          if (type === "text") {
            const fontSize = clamp(num(layerCandidate.fontSize, 72), 6, 400);
            layers.push({
              ...base,
              text: String(layerCandidate.text ?? "双击编辑文字").slice(0, 5000),
              fontFamily: String(layerCandidate.fontFamily || DEFAULT_TEXT_FONT).slice(0, 200),
              fontSize,
              fontWeight: clamp(Math.round(num(layerCandidate.fontWeight, 700) / 100) * 100, 100, 900),
              color: normalizeColor(layerCandidate.color),
              lineHeight: clamp(num(layerCandidate.lineHeight, 1.2), 0.7, 3)
            });
          } else {
            layers.push({ ...base, assetId });
          }
        }
      }

      return {
        id: normalizeId(candidate.id, "page"),
        name: String(candidate.name || `页面 ${pageIndex + 1}`).slice(0, 80),
        width,
        height,
        layers
      };
    });

    const usedPageIds = new Set();
    for (const page of pages) {
      if (usedPageIds.has(page.id)) page.id = uid("page");
      usedPageIds.add(page.id);
    }

    return {
      format: APP_FORMAT,
      version: APP_VERSION,
      updatedAt: num(raw.updatedAt, Date.now()),
      pages,
      assets
    };
  }

  function getCurrentPage() {
    if (!state.project) return null;
    return state.project.pages.find((page) => page.id === state.currentPageId) || state.project.pages[0] || null;
  }

  function getSelectedLayer() {
    const page = getCurrentPage();
    if (!page || !state.selectedLayerId) return null;
    return page.layers.find((layer) => layer.id === state.selectedLayerId) || null;
  }

  function getAsset(assetId) {
    if (!state.project) return null;
    return state.project.assets.find((asset) => asset.id === assetId) || null;
  }

  function isTextLayer(layer) {
    return Boolean(layer && layer.type === "text");
  }

  function getLayerDisplayName(layer) {
    if (!layer) return "";
    if (isTextLayer(layer)) {
      const firstLine = String(layer.text || "").split(/\r?\n/).map((line) => line.trim()).find(Boolean);
      return (firstLine || "文字").slice(0, 32);
    }
    return layer.name || "图片";
  }

  function measureTextHeight(layer, width = layer.w) {
    const probe = document.createElement("div");
    probe.style.position = "absolute";
    probe.style.visibility = "hidden";
    probe.style.pointerEvents = "none";
    probe.style.left = "-100000px";
    probe.style.top = "0";
    probe.style.width = `${Math.max(1, width)}px`;
    probe.style.boxSizing = "border-box";
    probe.style.fontFamily = layer.fontFamily || DEFAULT_TEXT_FONT;
    probe.style.fontSize = `${layer.fontSize || 72}px`;
    probe.style.fontWeight = String(layer.fontWeight || 700);
    probe.style.lineHeight = String(layer.lineHeight || 1.2);
    probe.style.whiteSpace = "pre-wrap";
    probe.style.overflowWrap = "anywhere";
    probe.style.wordBreak = "break-word";
    probe.textContent = String(layer.text || " ");
    document.body.append(probe);
    const height = probe.getBoundingClientRect().height;
    probe.remove();
    return Math.max((layer.fontSize || 72) * (layer.lineHeight || 1.2), Math.ceil(height));
  }

  function fitTextLayerHeight(layer) {
    layer.h = measureTextHeight(layer, layer.w);
  }

  // ---------------------------------------------------------------------------
  // Saving and cross-window synchronisation
  // ---------------------------------------------------------------------------

  function setSaveStatus(kind, text) {
    el.saveState.dataset.state = kind;
    el.saveText.textContent = text;
  }

  function scheduleSave(delay = 420) {
    if (!state.project) return;
    state.dirty = true;
    setSaveStatus("saving", "保存中…");
    if (saveTimer) clearTimeout(saveTimer);
    saveTimer = window.setTimeout(() => {
      saveTimer = null;
      saveNow();
    }, delay);
  }

  async function saveNow() {
    if (!state.project) return;
    if (state.interaction) {
      saveAgain = true;
      return;
    }
    if (savePromise) {
      saveAgain = true;
      return savePromise;
    }

    if (saveTimer) {
      clearTimeout(saveTimer);
      saveTimer = null;
    }

    state.dirty = false;
    setSaveStatus("saving", "保存中…");

    savePromise = (async () => {
      try {
        const result = await Store.save(state.project);
        state.revision = result.revision;
        if (state.dirty) {
          saveAgain = true;
          setSaveStatus("saving", "保存中…");
        } else {
          setSaveStatus("saved", `已保存 ${formatTime(result.updatedAt)}`);
        }
        if (syncChannel) {
          try {
            syncChannel.postMessage({
              type: "saved",
              source: clientId,
              revision: result.revision
            });
          } catch (_) {
            /* ignore */
          }
        }
      } catch (error) {
        state.dirty = true;
        setSaveStatus("error", "保存失败，自动重试");
        toast(`保存失败：${error.message || "请检查本地服务"}`, "error", 4200);
        if (!saveTimer) {
          saveTimer = window.setTimeout(() => {
            saveTimer = null;
            saveNow();
          }, 2800);
        }
      } finally {
        savePromise = null;
        if ((saveAgain || state.dirty) && !saveTimer) {
          saveAgain = false;
          scheduleSave(120);
        }
      }
    })();

    return savePromise;
  }

  function startSync() {
    if ("BroadcastChannel" in window) {
      try {
        syncChannel = new BroadcastChannel(SYNC_CHANNEL);
        syncChannel.addEventListener("message", (event) => {
          const data = event.data || {};
          if (data.type === "saved" && data.source !== clientId) {
            checkRemote().catch(() => {});
          }
        });
      } catch (_) {
        syncChannel = null;
      }
    }

    window.addEventListener("storage", (event) => {
      if (event.key === Store.metaKey) {
        checkRemote().catch(() => {});
      }
    });

    window.addEventListener("focus", () => {
      checkRemote().catch(() => {});
    });

    window.addEventListener("blur", () => {
      if (state.editingTextLayerId) commitTextEditing();
    });

    document.addEventListener("focusin", (event) => {
      if (!state.editingTextLayerId) return;
      const editingNode = el.canvas.querySelector(".canvas-layer.is-editing");
      if (editingNode && !editingNode.contains(event.target)) commitTextEditing();
    });

    document.addEventListener("visibilitychange", () => {
      if (document.hidden) {
        if (state.editingTextLayerId) commitTextEditing();
        if (state.dirty) saveNow();
      } else {
        checkRemote().catch(() => {});
      }
    });

    pollTimer = window.setInterval(() => {
      if (!document.hidden) checkRemote().catch(() => {});
    }, 1800);
  }

  async function checkRemote() {
    if (!state.initialized || !state.project || remoteChecking) return;
    if (state.dirty || savePromise || state.interaction || isTyping()) return;

    remoteChecking = true;
    try {
      const meta = await Store.meta();
      if (!meta || num(meta.revision, 0) <= state.revision) return;

      const loaded = await Store.load();
      if (!loaded || !loaded.project || num(loaded.revision, 0) <= state.revision) return;
      if (state.dirty || savePromise || state.interaction || isTyping()) return;

      const normalized = normalizeProject(loaded.project);
      const previousPageId = state.currentPageId;
      state.project = normalized;
      state.revision = num(loaded.revision, 0);
      state.currentPageId = normalized.pages.some((page) => page.id === previousPageId)
        ? previousPageId
        : normalized.pages[0].id;
      state.selectedLayerId = null;
      state.renamingPageId = null;
      state.undoStack = [];
      state.redoStack = [];
      updatePageHash();
      renderAll();
      if (state.zoomMode === "fit") fitCanvas();

      if (!el.previewModal.hidden) {
        state.previewIndex = clamp(state.previewIndex, 0, normalized.pages.length - 1);
        renderPreview();
        fitPreview();
      }

      setSaveStatus("saved", `已同步 ${formatTime(loaded.updatedAt || Date.now())}`);
      toast("已同步其他窗口的最新内容", "success", 2200);
    } catch (_) {
      /* next poll will retry */
    } finally {
      remoteChecking = false;
    }
  }

  // ---------------------------------------------------------------------------
  // Undo / redo
  // ---------------------------------------------------------------------------

  function snapshotState() {
    return {
      pages: deepClone(state.project.pages),
      currentPageId: state.currentPageId,
      selectedLayerId: state.selectedLayerId
    };
  }

  function pushHistory(before) {
    if (!before) return;
    state.undoStack.push(before);
    if (state.undoStack.length > 60) state.undoStack.shift();
    state.redoStack.length = 0;
    updateHistoryButtons();
  }

  function updateHistoryButtons() {
    el.undoBtn.disabled = state.undoStack.length === 0;
    el.redoBtn.disabled = state.redoStack.length === 0;
  }

  function restoreSnapshot(snapshot) {
    state.project.pages = snapshot.pages;
    state.currentPageId = state.project.pages.some((page) => page.id === snapshot.currentPageId)
      ? snapshot.currentPageId
      : state.project.pages[0].id;
    const page = getCurrentPage();
    state.selectedLayerId = page && snapshot.selectedLayerId &&
      page.layers.some((layer) => layer.id === snapshot.selectedLayerId)
      ? snapshot.selectedLayerId
      : null;
    state.renamingPageId = null;
    state.interaction = null;
    updatePageHash();
    renderAll();
    scheduleSave();
    updateHistoryButtons();
  }

  function undo() {
    if (!state.undoStack.length) return;
    const previous = state.undoStack.pop();
    state.redoStack.push(snapshotState());
    restoreSnapshot(previous);
    toast("已撤销", "", 1300);
  }

  function redo() {
    if (!state.redoStack.length) return;
    const next = state.redoStack.pop();
    state.undoStack.push(snapshotState());
    restoreSnapshot(next);
    toast("已重做", "", 1300);
  }

  // ---------------------------------------------------------------------------
  // Notices
  // ---------------------------------------------------------------------------

  function toast(message, type = "", duration = 2600) {
    if (!el.toastStack) return;
    while (el.toastStack.children.length >= 4) {
      el.toastStack.firstElementChild.remove();
    }

    const node = document.createElement("div");
    node.className = `toast ${type}`.trim();
    const dot = document.createElement("span");
    dot.className = "toast-dot";
    const text = document.createElement("span");
    text.textContent = message;
    node.append(dot, text);
    el.toastStack.append(node);

    window.setTimeout(() => {
      node.classList.add("is-out");
      window.setTimeout(() => node.remove(), 200);
    }, duration);
  }

  // ---------------------------------------------------------------------------
  // Page hash / current page
  // ---------------------------------------------------------------------------

  function updatePageHash() {
    const page = getCurrentPage();
    if (!page) return;
    try {
      history.replaceState(null, "", `#page=${encodeURIComponent(page.id)}`);
    } catch (_) {
      /* file:// can reject history changes */
    }
  }

  function pageFromHash() {
    try {
      const value = new URLSearchParams(location.hash.slice(1)).get("page");
      return value ? decodeURIComponent(value) : null;
    } catch (_) {
      return null;
    }
  }

  function ensureCurrentPage() {
    if (!state.project) return;
    if (!state.project.pages.some((page) => page.id === state.currentPageId)) {
      state.currentPageId = state.project.pages[0].id;
      state.selectedLayerId = null;
    }
  }

  function switchPage(pageId) {
    if (!state.project || state.currentPageId === pageId) return;
    if (!state.project.pages.some((page) => page.id === pageId)) return;
    state.currentPageId = pageId;
    state.selectedLayerId = null;
    state.renamingPageId = null;
    updatePageHash();
    renderAll();
    if (state.zoomMode === "fit") fitCanvas();
  }

  function addPage() {
    if (!state.project) return;
    const source = getCurrentPage();
    const before = snapshotState();
    let index = state.project.pages.length + 1;
    const names = new Set(state.project.pages.map((page) => page.name));
    while (names.has(`页面 ${index}`)) index += 1;
    const page = createPage(`页面 ${index}`, source ? source.width : DEFAULT_WIDTH, source ? source.height : DEFAULT_HEIGHT);
    state.project.pages.push(page);
    state.currentPageId = page.id;
    state.selectedLayerId = null;
    state.renamingPageId = null;
    pushHistory(before);
    updatePageHash();
    renderAll();
    scheduleSave();
    if (state.zoomMode === "fit") fitCanvas();
    toast(`已新建「${page.name}」`, "success", 1600);
  }

  function deletePage(pageId) {
    if (!state.project) return;
    const page = state.project.pages.find((candidate) => candidate.id === pageId);
    if (!page) return;
    if (state.project.pages.length <= 1) {
      toast("至少需要保留一个页面", "error");
      return;
    }
    if (!window.confirm(`删除「${page.name}」？画布上的图层会一并删除，但素材库中的原图片会保留。`)) {
      return;
    }

    const before = snapshotState();
    const index = state.project.pages.findIndex((candidate) => candidate.id === pageId);
    state.project.pages.splice(index, 1);
    if (state.currentPageId === pageId) {
      const next = state.project.pages[Math.min(index, state.project.pages.length - 1)];
      state.currentPageId = next.id;
      state.selectedLayerId = null;
    }
    state.renamingPageId = null;
    pushHistory(before);
    updatePageHash();
    renderAll();
    scheduleSave();
    toast("页面已删除，素材仍保留", "", 2200);
  }

  function startPageRename(pageId) {
    if (!state.project || !state.project.pages.some((page) => page.id === pageId)) return;
    state.renamingPageId = pageId;
    renderPages();
    const input = el.pagesList.querySelector(`[data-page-id="${pageId}"] .page-rename`);
    if (input) {
      input.focus();
      input.select();
    }
  }

  function commitPageRename(pageId, value) {
    if (state.renamingPageId !== pageId || !state.project) return;
    const page = state.project.pages.find((candidate) => candidate.id === pageId);
    const nextName = String(value || "").trim().slice(0, 80);
    state.renamingPageId = null;

    if (page && nextName && nextName !== page.name) {
      const before = snapshotState();
      page.name = nextName;
      pushHistory(before);
      scheduleSave();
    }
    renderPages();
    if (!el.previewModal.hidden) renderPreview();
  }

  // ---------------------------------------------------------------------------
  // Page list
  // ---------------------------------------------------------------------------

  function renderPages() {
    if (!state.project) return;
    el.pagesList.replaceChildren();
    el.pageCount.textContent = String(state.project.pages.length);

    state.project.pages.forEach((page, index) => {
      const row = document.createElement("div");
      row.className = "page-item" + (page.id === state.currentPageId ? " active" : "");
      row.dataset.pageId = page.id;
      row.tabIndex = 0;
      row.setAttribute("role", "button");
      row.setAttribute("aria-label", `切换到 ${page.name}`);

      const pageIndex = document.createElement("div");
      pageIndex.className = "page-index";
      pageIndex.textContent = String(index + 1).padStart(2, "0");

      const info = document.createElement("div");
      info.className = "page-info";

      if (state.renamingPageId === page.id) {
        const input = document.createElement("input");
        input.className = "page-rename";
        input.type = "text";
        input.maxLength = 80;
        input.value = page.name;
        input.setAttribute("aria-label", "重命名页面");
        input.addEventListener("click", (event) => event.stopPropagation());
        input.addEventListener("pointerdown", (event) => event.stopPropagation());
        input.addEventListener("keydown", (event) => {
          event.stopPropagation();
          if (event.key === "Enter") {
            event.preventDefault();
            input.blur();
          } else if (event.key === "Escape") {
            event.preventDefault();
            state.renamingPageId = null;
            renderPages();
          }
        });
        input.addEventListener("blur", () => {
          if (state.renamingPageId === page.id) commitPageRename(page.id, input.value);
        });
        info.append(input);
      } else {
        const name = document.createElement("span");
        name.className = "page-name";
        name.textContent = page.name;
        const meta = document.createElement("span");
        meta.className = "page-meta";
        meta.textContent = `${page.layers.length} 个图层`;
        info.append(name, meta);
      }

      const actions = document.createElement("div");
      actions.className = "page-actions";

      const renameBtn = document.createElement("button");
      renameBtn.type = "button";
      renameBtn.className = "page-action";
      renameBtn.title = "重命名页面";
      renameBtn.innerHTML = icon("pencil");
      renameBtn.addEventListener("click", (event) => {
        event.stopPropagation();
        startPageRename(page.id);
      });

      const deleteBtn = document.createElement("button");
      deleteBtn.type = "button";
      deleteBtn.className = "page-action danger";
      deleteBtn.title = state.project.pages.length <= 1 ? "至少保留一个页面" : "删除页面";
      deleteBtn.innerHTML = icon("trash");
      deleteBtn.disabled = state.project.pages.length <= 1;
      deleteBtn.addEventListener("click", (event) => {
        event.stopPropagation();
        deletePage(page.id);
      });

      actions.append(renameBtn, deleteBtn);
      row.append(pageIndex, info, actions);

      row.addEventListener("click", (event) => {
        if (event.target.closest("button") || event.target.classList.contains("page-rename")) return;
        switchPage(page.id);
      });
      row.addEventListener("dblclick", (event) => {
        if (event.target.closest("button") || event.target.classList.contains("page-rename")) return;
        startPageRename(page.id);
      });
      row.addEventListener("keydown", (event) => {
        if (event.target === row && (event.key === "Enter" || event.key === " ")) {
          event.preventDefault();
          switchPage(page.id);
        }
      });

      el.pagesList.append(row);
    });

    if (state.renamingPageId) {
      const input = el.pagesList.querySelector(".page-rename");
      if (input && document.activeElement !== input) {
        input.focus();
        input.select();
      }
    }
  }

  // ---------------------------------------------------------------------------
  // Asset library
  // ---------------------------------------------------------------------------

  function renderAssets() {
    if (!state.project) return;
    el.assetsGrid.replaceChildren();
    const assets = state.project.assets;
    el.assetCount.textContent = String(assets.length);
    el.assetEmpty.hidden = assets.length > 0;
    el.assetsGrid.hidden = assets.length === 0;

    for (const asset of [...assets].reverse()) {
      const card = document.createElement("button");
      card.type = "button";
      card.className = "asset-card";
      card.dataset.assetId = asset.id;
      card.title = `${asset.name} · ${Math.round(asset.width)} × ${Math.round(asset.height)}\n点击添加到画布`;

      const thumb = document.createElement("span");
      thumb.className = "asset-thumb";
      const image = document.createElement("img");
      image.src = asset.dataUrl;
      image.alt = "";
      image.draggable = false;
      thumb.append(image);

      const add = document.createElement("span");
      add.className = "asset-add";
      add.textContent = "+";

      const name = document.createElement("span");
      name.className = "asset-name";
      name.textContent = asset.name;

      card.append(thumb, add, name);
      card.addEventListener("click", () => addAssetToCanvas(asset.id));
      el.assetsGrid.append(card);
    }
  }

  async function handleAssetFiles(fileList) {
    if (!state.project) return;
    const files = Array.from(fileList || []).filter((file) => {
      if (!file) return false;
      if (file.type && file.type.startsWith("image/")) return true;
      return /\.(png|webp|svg|gif|jpe?g|avif)$/i.test(file.name || "");
    });

    if (!files.length) {
      toast("请选择图片文件", "error");
      return;
    }

    el.uploadBtn.disabled = true;
    let added = 0;
    let failed = 0;

    for (const file of files) {
      try {
        if (file.size > 60 * 1024 * 1024) throw new Error("单张图片不能超过 60MB");
        const dataUrl = await readFileAsDataURL(file);
        if (!dataUrl.startsWith("data:image/")) throw new Error("不是有效的图片");
        const image = await loadImage(dataUrl);
        state.project.assets.push({
          id: uid("asset"),
          name: stripExtension(file.name),
          dataUrl,
          width: Math.max(1, image.naturalWidth || image.width || 1000),
          height: Math.max(1, image.naturalHeight || image.height || 1000),
          type: file.type || "image/png",
          size: file.size,
          createdAt: Date.now()
        });
        added += 1;
      } catch (_) {
        failed += 1;
      }
    }

    el.uploadBtn.disabled = false;
    el.assetInput.value = "";
    el.assetDropZone.classList.remove("dragover");
    dragFileDepth = 0;

    if (added) {
      renderAssets();
      scheduleSave();
      toast(`已上传 ${added} 张素材`, "success", 2000);
    }
    if (failed) {
      toast(`${failed} 张图片读取失败`, "error", 3200);
    }
  }

  function addAssetToCanvas(assetId) {
    if (!state.project) return;
    const page = getCurrentPage();
    const asset = getAsset(assetId);
    if (!page || !asset) return;

    const maxWidth = page.width * 0.46;
    const maxHeight = page.height * 0.56;
    const fit = Math.min(1, maxWidth / asset.width, maxHeight / asset.height);
    const width = Math.max(8, asset.width * fit);
    const height = Math.max(8, asset.height * fit);

    const before = snapshotState();
    const layer = {
      id: uid("layer"),
      type: "image",
      assetId: asset.id,
      name: asset.name,
      x: (page.width - width) / 2,
      y: (page.height - height) / 2,
      w: width,
      h: height,
      rotation: 0,
      hidden: false,
      locked: false
    };
    page.layers.push(layer);
    state.selectedLayerId = layer.id;
    pushHistory(before);
    renderAll();
    scheduleSave();
    scrollSelectedLayerIntoView();
  }

  // ---------------------------------------------------------------------------
  // Text layers and text inspector
  // ---------------------------------------------------------------------------

  function addTextLayer() {
    if (!state.project) return;
    const page = getCurrentPage();
    if (!page) return;

    const layer = {
      id: uid("layer"),
      type: "text",
      name: "文字",
      text: "双击编辑文字",
      fontFamily: DEFAULT_TEXT_FONT,
      fontSize: 72,
      fontWeight: 700,
      color: DEFAULT_TEXT_COLOR,
      lineHeight: 1.2,
      x: 0,
      y: 0,
      w: 480,
      h: 96,
      rotation: 0,
      hidden: false,
      locked: false
    };
    fitTextLayerHeight(layer);
    layer.x = (page.width - layer.w) / 2;
    layer.y = (page.height - layer.h) / 2;

    const before = snapshotState();
    page.layers.push(layer);
    state.selectedLayerId = layer.id;
    pushHistory(before);
    renderAll();
    scheduleSave();
    beginTextEditing(layer.id, true);
  }

  function syncTextInspector() {
    const layer = getSelectedLayer();
    const show = isTextLayer(layer);
    el.textInspector.hidden = !show;
    if (!show) return;

    const disabled = Boolean(layer.locked);
    const options = Array.from(el.textFontSelect.options);
    if (!options.some((option) => option.value === layer.fontFamily)) {
      let custom = el.textFontSelect.querySelector("option[data-custom]");
      if (!custom) {
        custom = document.createElement("option");
        custom.dataset.custom = "1";
        el.textFontSelect.append(custom);
      }
      custom.value = layer.fontFamily;
      custom.textContent = "自定义字体";
    }
    if (document.activeElement !== el.textFontSelect) {
      el.textFontSelect.value = layer.fontFamily;
    }

    if (document.activeElement !== el.fontSizeInput) {
      el.fontSizeInput.value = String(Math.round(layer.fontSize * 10) / 10);
    }
    el.fontSizeInput.disabled = disabled;
    el.textFontSelect.disabled = disabled;
    el.fontSizeMinus.disabled = disabled;
    el.fontSizePlus.disabled = disabled;
    el.textBoldBtn.disabled = disabled;
    el.textColorInput.disabled = disabled;
    const colorButton = el.textColorInput.closest(".color-btn");
    if (colorButton) colorButton.classList.toggle("is-disabled", disabled);
    el.textBoldBtn.classList.toggle("is-active", layer.fontWeight >= 600);
    el.textColorInput.value = normalizeColor(layer.color);
    el.textColorDot.style.background = normalizeColor(layer.color);
  }

  function applyTextFontSize(nextSize) {
    const layer = getSelectedLayer();
    if (!isTextLayer(layer) || layer.locked) return;
    const next = clamp(Math.round(num(nextSize, layer.fontSize) * 10) / 10, 6, 400);
    if (Math.abs(next - layer.fontSize) < 0.01) {
      el.fontSizeInput.value = String(Math.round(layer.fontSize * 10) / 10);
      syncTextInspector();
      return;
    }

    el.fontSizeInput.value = String(next);
    const before = snapshotState();
    const ratio = next / layer.fontSize;
    layer.fontSize = next;
    layer.w = Math.max(MIN_LAYER_SIZE, layer.w * ratio);
    layer.h = Math.max(MIN_LAYER_SIZE, layer.h * ratio);
    pushHistory(before);
    renderCanvas();
    updateSelection();
    scheduleSave();
    syncTextInspector();
  }

  function updateSelectedTextStyle(mutator, measure = false) {
    const layer = getSelectedLayer();
    if (!isTextLayer(layer) || layer.locked) return;
    const before = snapshotState();
    mutator(layer);
    if (measure) fitTextLayerHeight(layer);
    pushHistory(before);
    renderCanvas();
    renderLayers();
    updateSelection();
    scheduleSave();
    syncTextInspector();
  }

  function beginTextEditing(layerId, selectAll = false) {
    const page = getCurrentPage();
    const layer = page && page.layers.find((candidate) => candidate.id === layerId);
    if (!isTextLayer(layer) || layer.hidden || layer.locked) return;

    if (state.editingTextLayerId && state.editingTextLayerId !== layerId) {
      commitTextEditing();
    }

    state.selectedLayerId = layerId;
    state.editingTextLayerId = layerId;
    state.textEditBefore = snapshotState();
    state.textEditCancel = false;
    renderLayers();
    updateSelection();

    const node = Array.from(el.canvas.querySelectorAll(".canvas-layer"))
      .find((candidate) => candidate.dataset.layerId === layerId);
    const content = node && node.querySelector(".text-content");
    if (!node || !content) return;

    node.classList.add("is-editing");
    content.setAttribute("contenteditable", "true");
    content.spellcheck = false;
    content.focus({ preventScroll: true });

    if (selectAll) {
      const range = document.createRange();
      range.selectNodeContents(content);
      const selection = window.getSelection();
      selection.removeAllRanges();
      selection.addRange(range);
    }

    content.onblur = () => {
      if (state.editingTextLayerId === layerId) commitTextEditing();
    };
    content.onkeydown = (event) => {
      event.stopPropagation();
      if (event.key === "Escape") {
        event.preventDefault();
        state.textEditCancel = true;
        content.blur();
      } else if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
        event.preventDefault();
        content.blur();
      }
    };
    content.onpaste = (event) => {
      event.preventDefault();
      const text = (event.clipboardData || window.clipboardData).getData("text/plain");
      document.execCommand("insertText", false, text);
    };
  }

  function commitTextEditing() {
    const layerId = state.editingTextLayerId;
    if (!layerId) return;

    const cancel = state.textEditCancel;
    const before = state.textEditBefore;
    state.editingTextLayerId = null;
    state.textEditCancel = false;
    state.textEditBefore = null;

    const page = getCurrentPage();
    const layer = page && page.layers.find((candidate) => candidate.id === layerId);
    const node = Array.from(el.canvas.querySelectorAll(".canvas-layer"))
      .find((candidate) => candidate.dataset.layerId === layerId);
    const content = node && node.querySelector(".text-content");
    let nextText = content ? content.innerText.replace(/\r/g, "").replace(/\u00a0/g, " ") : "";

    if (!cancel && layer) {
      if (!nextText.trim()) nextText = "文字";
      if (nextText !== layer.text) {
        layer.text = nextText;
        fitTextLayerHeight(layer);
        if (before) pushHistory(before);
        scheduleSave();
      }
    }

    renderCanvas();
    renderLayers();
    updateSelection();
    syncTextInspector();
  }

  function adjustTextFontSize(delta) {
    const layer = getSelectedLayer();
    if (!isTextLayer(layer) || layer.locked) return;
    applyTextFontSize(Math.round(layer.fontSize) + delta);
  }

  // ---------------------------------------------------------------------------
  // Layer operations
  // ---------------------------------------------------------------------------

  function selectLayer(layerId) {
    if (state.selectedLayerId === layerId) {
      updateSelection();
      return;
    }
    state.selectedLayerId = layerId;
    renderLayers();
    updateSelection();
    scrollSelectedLayerIntoView();
  }

  function scrollSelectedLayerIntoView() {
    if (!state.selectedLayerId) return;
    requestAnimationFrame(() => {
      const row = el.layersList.querySelector(`[data-layer-id="${state.selectedLayerId}"]`);
      if (row) row.scrollIntoView({ block: "nearest" });
    });
  }

  function toggleLayerHidden(layerId) {
    const page = getCurrentPage();
    const layer = page && page.layers.find((candidate) => candidate.id === layerId);
    if (!layer) return;
    const before = snapshotState();
    layer.hidden = !layer.hidden;
    pushHistory(before);
    renderCanvas();
    renderLayers();
    updateSelection();
    scheduleSave();
  }

  function toggleLayerLocked(layerId) {
    const page = getCurrentPage();
    const layer = page && page.layers.find((candidate) => candidate.id === layerId);
    if (!layer) return;
    const before = snapshotState();
    layer.locked = !layer.locked;
    pushHistory(before);
    renderCanvas();
    renderLayers();
    updateSelection();
    scheduleSave();
  }

  function deleteLayer(layerId) {
    const page = getCurrentPage();
    const layer = page && page.layers.find((candidate) => candidate.id === layerId);
    if (!page || !layer) return;
    if (layer.locked) {
      toast("图层已锁定，请先解锁再删除", "error", 2200);
      return;
    }

    const before = snapshotState();
    page.layers = page.layers.filter((candidate) => candidate.id !== layerId);
    if (state.selectedLayerId === layerId) state.selectedLayerId = null;
    pushHistory(before);
    renderAll();
    scheduleSave();
  }

  function reorderLayer(dragId, targetId, after) {
    const page = getCurrentPage();
    if (!page || dragId === targetId) return;

    const displayOrder = [...page.layers].reverse();
    const from = displayOrder.findIndex((layer) => layer.id === dragId);
    if (from < 0) return;
    const [moved] = displayOrder.splice(from, 1);
    let to = displayOrder.findIndex((layer) => layer.id === targetId);
    if (to < 0) to = displayOrder.length;
    else if (after) to += 1;
    displayOrder.splice(to, 0, moved);

    const nextLayers = displayOrder.reverse();
    const currentIds = page.layers.map((layer) => layer.id).join("|");
    const nextIds = nextLayers.map((layer) => layer.id).join("|");
    if (currentIds === nextIds) return;

    const before = snapshotState();
    page.layers = nextLayers;
    pushHistory(before);
    renderLayers();
    renderCanvas();
    updateSelection();
    scheduleSave();
  }

  // ---------------------------------------------------------------------------
  // Layer list
  // ---------------------------------------------------------------------------

  function renderLayers() {
    if (!state.project) return;
    const page = getCurrentPage();
    el.layersList.replaceChildren();
    el.layerCount.textContent = page ? String(page.layers.length) : "0";
    el.layersEmpty.hidden = Boolean(page && page.layers.length);

    if (!page || !page.layers.length) return;

    for (const layer of [...page.layers].reverse()) {
      const textLayer = isTextLayer(layer);
      const asset = textLayer ? null : getAsset(layer.assetId);
      if (!textLayer && !asset) continue;
      const displayName = getLayerDisplayName(layer);

      const row = document.createElement("div");
      row.className = "layer-item" +
        (layer.id === state.selectedLayerId ? " active" : "") +
        (layer.hidden ? " is-hidden" : "");
      row.dataset.layerId = layer.id;
      row.tabIndex = 0;
      row.draggable = true;
      row.setAttribute("aria-label", `图层 ${displayName}`);

      const grip = document.createElement("span");
      grip.className = "layer-grip";
      grip.innerHTML = icon("grip");

      const thumb = document.createElement("span");
      thumb.className = "layer-thumb" + (textLayer ? " text-thumb" : "");
      if (textLayer) {
        thumb.textContent = "T";
      } else {
        const image = document.createElement("img");
        image.src = asset.dataUrl;
        image.alt = "";
        image.draggable = false;
        thumb.append(image);
      }

      const name = document.createElement("span");
      name.className = "layer-name";
      name.textContent = displayName;
      name.title = displayName;

      const actions = document.createElement("span");
      actions.className = "layer-actions";

      const hideBtn = document.createElement("button");
      hideBtn.type = "button";
      hideBtn.className = "layer-btn" + (layer.hidden ? " is-on" : "");
      hideBtn.title = layer.hidden ? "显示图层" : "隐藏图层";
      hideBtn.innerHTML = icon(layer.hidden ? "eye-off" : "eye");
      hideBtn.addEventListener("click", (event) => {
        event.stopPropagation();
        toggleLayerHidden(layer.id);
      });

      const lockBtn = document.createElement("button");
      lockBtn.type = "button";
      lockBtn.className = "layer-btn" + (layer.locked ? " is-locked" : "");
      lockBtn.title = layer.locked ? "解锁图层" : "锁定图层";
      lockBtn.innerHTML = icon(layer.locked ? "lock" : "unlock");
      lockBtn.addEventListener("click", (event) => {
        event.stopPropagation();
        toggleLayerLocked(layer.id);
      });

      const deleteBtn = document.createElement("button");
      deleteBtn.type = "button";
      deleteBtn.className = "layer-btn delete";
      deleteBtn.title = layer.locked ? "请先解锁" : (textLayer ? "删除文字图层" : "删除图层（素材仍保留）");
      deleteBtn.innerHTML = icon("trash");
      deleteBtn.disabled = layer.locked;
      deleteBtn.addEventListener("click", (event) => {
        event.stopPropagation();
        deleteLayer(layer.id);
      });

      actions.append(hideBtn, lockBtn, deleteBtn);
      row.append(grip, thumb, name, actions);

      row.addEventListener("click", (event) => {
        if (event.target.closest("button")) return;
        selectLayer(layer.id);
      });
      row.addEventListener("keydown", (event) => {
        if (event.target === row && (event.key === "Enter" || event.key === " ")) {
          event.preventDefault();
          selectLayer(layer.id);
        }
      });

      row.addEventListener("dragstart", (event) => {
        state.dragLayerId = layer.id;
        state.dropTarget = null;
        if (event.dataTransfer) {
          event.dataTransfer.effectAllowed = "move";
          try {
            event.dataTransfer.setData("text/plain", layer.id);
          } catch (_) {
            /* ignore */
          }
        }
        row.classList.add("dragging");
      });

      row.addEventListener("dragend", () => {
        state.dragLayerId = null;
        state.dropTarget = null;
        clearDropIndicators();
        row.classList.remove("dragging");
      });

      row.addEventListener("dragover", (event) => {
        if (!state.dragLayerId) return;
        event.preventDefault();
        const rect = row.getBoundingClientRect();
        const after = event.clientY > rect.top + rect.height / 2;
        state.dropTarget = { id: layer.id, after };
        clearDropIndicators();
        row.classList.add(after ? "drop-after" : "drop-before");
        if (event.dataTransfer) event.dataTransfer.dropEffect = "move";
      });

      row.addEventListener("drop", (event) => {
        if (!state.dragLayerId) return;
        event.preventDefault();
        const rect = row.getBoundingClientRect();
        const after = event.clientY > rect.top + rect.height / 2;
        const dragId = state.dragLayerId;
        state.dragLayerId = null;
        state.dropTarget = null;
        clearDropIndicators();
        reorderLayer(dragId, layer.id, after);
      });

      el.layersList.append(row);
    }
  }

  function clearDropIndicators() {
    el.layersList.querySelectorAll(".drop-before, .drop-after").forEach((node) => {
      node.classList.remove("drop-before", "drop-after");
    });
  }

  // ---------------------------------------------------------------------------
  // Canvas rendering and selection
  // ---------------------------------------------------------------------------

  function renderAll() {
    if (!state.project) return;
    ensureCurrentPage();
    renderPages();
    renderAssets();
    renderLayers();
    applyCanvasSize();
    renderCanvas();
    updateSelection();
    updateHistoryButtons();
  }

  function createLayerElement(layer, asset, zIndex, preview = false) {
    const node = document.createElement("div");
    node.className = "canvas-layer" + (!preview && layer.locked ? " is-locked" : "");
    node.dataset.layerId = layer.id;
    node.style.left = `${layer.x}px`;
    node.style.top = `${layer.y}px`;
    node.style.width = `${layer.w}px`;
    node.style.height = `${layer.h}px`;
    node.style.transform = `rotate(${layer.rotation}deg)`;
    node.style.zIndex = String(zIndex);
    if (!preview) node.title = getLayerDisplayName(layer);

    const image = document.createElement("img");
    image.src = asset.dataUrl;
    image.alt = "";
    image.draggable = false;
    image.decoding = "async";
    node.append(image);
    return node;
  }

  function createTextLayerElement(layer, zIndex, preview = false) {
    const node = document.createElement("div");
    node.className = "canvas-layer text-layer" + (!preview && layer.locked ? " is-locked" : "");
    node.dataset.layerId = layer.id;
    node.style.left = `${layer.x}px`;
    node.style.top = `${layer.y}px`;
    node.style.width = `${layer.w}px`;
    node.style.height = `${layer.h}px`;
    node.style.transform = `rotate(${layer.rotation}deg)`;
    node.style.zIndex = String(zIndex);
    if (!preview) node.title = `${getLayerDisplayName(layer)}\n双击编辑文字`;

    const content = document.createElement("div");
    content.className = "text-content";
    content.textContent = layer.text;
    content.style.fontFamily = layer.fontFamily;
    content.style.fontSize = `${layer.fontSize}px`;
    content.style.fontWeight = String(layer.fontWeight);
    content.style.lineHeight = String(layer.lineHeight);
    content.style.color = layer.color;
    node.append(content);
    return node;
  }

  function createEmptyHint(page) {
    const wrapper = document.createElement("div");
    wrapper.className = "canvas-empty";

    const orb = document.createElement("div");
    orb.className = "empty-orb";
    orb.innerHTML = icon("image");

    const title = document.createElement("strong");
    const description = document.createElement("span");

    if (page.layers.length) {
      title.textContent = "所有图层已隐藏";
      description.textContent = "在右侧图层列表中点击眼睛图标，即可重新显示。";
      wrapper.append(orb, title, description);
      return wrapper;
    }

    if (state.project.assets.length) {
      title.textContent = "点击素材开始排版";
      description.textContent = "从左侧素材库点选图片，它会出现在画布中央。";
    } else {
      title.textContent = "上传透明图片开始";
      description.textContent = "支持 PNG / WebP / SVG / GIF，上传后可反复添加到任意页面。";
    }

    const button = document.createElement("button");
    button.type = "button";
    button.className = "btn";
    button.textContent = state.project.assets.length ? "上传更多图片" : "选择图片";
    button.addEventListener("pointerdown", (event) => event.stopPropagation());
    button.addEventListener("click", (event) => {
      event.stopPropagation();
      el.assetInput.click();
    });

    wrapper.append(orb, title, description, button);
    return wrapper;
  }

  function renderCanvas() {
    const page = getCurrentPage();
    el.canvas.replaceChildren();
    if (!page) return;

    let visibleCount = 0;
    page.layers.forEach((layer, index) => {
      if (layer.hidden) return;
      if (isTextLayer(layer)) {
        visibleCount += 1;
        el.canvas.append(createTextLayerElement(layer, index + 1));
        return;
      }
      const asset = getAsset(layer.assetId);
      if (!asset) return;
      visibleCount += 1;
      el.canvas.append(createLayerElement(layer, asset, index + 1));
    });

    if (visibleCount === 0) {
      el.canvas.append(createEmptyHint(page));
    }
  }

  function applyCanvasSize() {
    const page = getCurrentPage();
    if (!page) return;

    const zoom = state.zoom;
    const inv = 1 / zoom;
    el.canvasFrame.style.width = `${page.width}px`;
    el.canvasFrame.style.height = `${page.height}px`;
    el.canvasFrame.style.transform = `scale(${zoom})`;
    el.canvasScale.style.width = `${page.width * zoom}px`;
    el.canvasScale.style.height = `${page.height * zoom}px`;
    el.zoomValueBtn.textContent = `${Math.round(zoom * 100)}%`;

    el.selectionOverlay.style.setProperty("--hs", `${10 * inv}px`);
    el.selectionOverlay.style.setProperty("--rot-offset", `${29 * inv}px`);
    el.selBadge.style.fontSize = `${11 * inv}px`;
    el.selBadge.style.padding = `${3 * inv}px ${6 * inv}px`;
    el.selBadge.style.borderRadius = `${5 * inv}px`;
  }

  function updateSelection() {
    const layer = getSelectedLayer();
    const hasContent = layer && (isTextLayer(layer) ? Boolean(layer.text) : Boolean(getAsset(layer.assetId)));
    if (!layer || layer.hidden || !hasContent) {
      el.selectionBox.classList.remove("is-visible", "is-locked");
      el.selBadge.classList.remove("is-visible");
      syncTextInspector();
      return;
    }

    el.selectionBox.style.left = `${layer.x}px`;
    el.selectionBox.style.top = `${layer.y}px`;
    el.selectionBox.style.width = `${layer.w}px`;
    el.selectionBox.style.height = `${layer.h}px`;
    el.selectionBox.style.transform = `rotate(${layer.rotation}deg)`;
    el.selectionBox.classList.add("is-visible");
    el.selectionBox.classList.toggle("is-locked", layer.locked);

    const interaction = state.interaction;
    if (interaction && (interaction.type === "scale" || interaction.type === "rotate") && !layer.locked) {
      el.selBadge.classList.add("is-visible");
    } else {
      el.selBadge.classList.remove("is-visible");
    }
    syncTextInspector();
  }

  function showSelectionBadge(text) {
    el.selBadge.textContent = text;
    el.selBadge.classList.add("is-visible");
  }

  function updateLayerVisual(layer) {
    const nodes = el.canvas.querySelectorAll(".canvas-layer");
    for (const node of nodes) {
      if (node.dataset.layerId !== layer.id) continue;
      node.style.left = `${layer.x}px`;
      node.style.top = `${layer.y}px`;
      node.style.width = `${layer.w}px`;
      node.style.height = `${layer.h}px`;
      node.style.transform = `rotate(${layer.rotation}deg)`;
      if (isTextLayer(layer)) {
        const content = node.querySelector(".text-content");
        if (content) content.style.fontSize = `${layer.fontSize}px`;
      }
      break;
    }
  }

  function clientToCanvas(event) {
    const rect = el.canvas.getBoundingClientRect();
    return {
      x: (event.clientX - rect.left) / state.zoom,
      y: (event.clientY - rect.top) / state.zoom
    };
  }

  // ---------------------------------------------------------------------------
  // Zoom
  // ---------------------------------------------------------------------------

  function setZoom(value, focal = null, mode = "manual") {
    if (!state.project) return;
    const next = clamp(value, 0.1, 3);
    let logical = null;

    if (focal) {
      const rectBefore = el.canvas.getBoundingClientRect();
      logical = {
        x: (focal.x - rectBefore.left) / state.zoom,
        y: (focal.y - rectBefore.top) / state.zoom
      };
    }

    state.zoom = next;
    state.zoomMode = mode;
    applyCanvasSize();
    updateSelection();

    if (focal && logical) {
      const rectAfter = el.canvas.getBoundingClientRect();
      el.workspaceScroll.scrollLeft += rectAfter.left + logical.x * next - focal.x;
      el.workspaceScroll.scrollTop += rectAfter.top + logical.y * next - focal.y;
    }
  }

  function fitCanvas() {
    const page = getCurrentPage();
    if (!page) return;
    const availableWidth = Math.max(180, el.workspaceScroll.clientWidth - 180);
    const availableHeight = Math.max(140, el.workspaceScroll.clientHeight - 205);
    const zoom = clamp(Math.min(availableWidth / page.width, availableHeight / page.height, 1.35), 0.1, 2.5);
    setZoom(zoom, null, "fit");
  }

  function zoomStep(direction) {
    const stops = [0.1, 0.15, 0.2, 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.25, 1.5, 2, 2.5, 3];
    if (direction > 0) {
      const next = stops.find((value) => value > state.zoom + 0.001) ?? stops[stops.length - 1];
      setZoom(next, null, "manual");
    } else {
      const reversed = [...stops].reverse();
      const next = reversed.find((value) => value < state.zoom - 0.001) ?? stops[0];
      setZoom(next, null, "manual");
    }
  }

  function onWorkspaceWheel(event) {
    if (!(event.ctrlKey || event.metaKey)) return;
    event.preventDefault();
    const factor = Math.exp(-event.deltaY * 0.0017);
    setZoom(state.zoom * factor, { x: event.clientX, y: event.clientY }, "manual");
  }

  // ---------------------------------------------------------------------------
  // Pointer interactions: move / scale / rotate
  // ---------------------------------------------------------------------------

  function onCanvasPointerDown(event) {
    if (event.button !== 0) return;
    const page = getCurrentPage();
    if (!page) return;

    if (state.editingTextLayerId) {
      const editingNode = el.canvas.querySelector(".canvas-layer.is-editing");
      if (editingNode && editingNode.contains(event.target)) return;
      commitTextEditing();
      return;
    }

    const layerNode = event.target.closest(".canvas-layer");
    if (!layerNode) {
      if (state.selectedLayerId) {
        state.selectedLayerId = null;
        renderLayers();
        updateSelection();
      }
      return;
    }

    const layer = page.layers.find((candidate) => candidate.id === layerNode.dataset.layerId);
    if (!layer) return;

    if (state.selectedLayerId !== layer.id) {
      state.selectedLayerId = layer.id;
      renderLayers();
      updateSelection();
      scrollSelectedLayerIntoView();
    }

    if (layer.locked || layer.hidden) return;

    event.preventDefault();
    state.interaction = {
      type: "move",
      layerId: layer.id,
      before: snapshotState(),
      startClientX: event.clientX,
      startClientY: event.clientY,
      startX: layer.x,
      startY: layer.y,
      changed: false
    };
    document.body.classList.add("is-dragging");
    document.body.style.cursor = "move";
  }

  function onSelectionPointerDown(event) {
    const handleNode = event.target.closest("[data-handle]");
    if (!handleNode) return;
    if (state.editingTextLayerId) {
      commitTextEditing();
      return;
    }
    const layer = getSelectedLayer();
    if (!layer || layer.locked || layer.hidden) return;

    event.preventDefault();
    event.stopPropagation();
    document.body.classList.add("is-dragging");
    const handle = handleNode.dataset.handle;

    if (handle === "rotate") {
      const point = clientToCanvas(event);
      const centerX = layer.x + layer.w / 2;
      const centerY = layer.y + layer.h / 2;
      state.interaction = {
        type: "rotate",
        layerId: layer.id,
        before: snapshotState(),
        centerX,
        centerY,
        startPointerAngle: Math.atan2(point.y - centerY, point.x - centerX) * 180 / Math.PI,
        startRotation: layer.rotation,
        changed: false
      };
      document.body.style.cursor = "grabbing";
      showSelectionBadge(`${Math.round(layer.rotation)}°`);
      return;
    }

    if (!["nw", "ne", "se", "sw"].includes(handle)) return;

    state.interaction = {
      type: "scale",
      layerId: layer.id,
      handle,
      before: snapshotState(),
      startLayer: {
        x: layer.x,
        y: layer.y,
        w: layer.w,
        h: layer.h,
        rotation: layer.rotation,
        fontSize: layer.fontSize || 72
      },
      changed: false
    };
    document.body.style.cursor = handle === "nw" || handle === "se" ? "nwse-resize" : "nesw-resize";
    showSelectionBadge(isTextLayer(layer) ? `字号 ${Math.round(layer.fontSize)}` : `${Math.round(layer.w)} × ${Math.round(layer.h)}`);
  }

  function onPointerMove(event) {
    const interaction = state.interaction;
    if (!interaction) return;
    const page = getCurrentPage();
    if (!page) return;
    const layer = page.layers.find((candidate) => candidate.id === interaction.layerId);
    if (!layer) return;

    event.preventDefault();

    if (interaction.type === "move") {
      let dx = (event.clientX - interaction.startClientX) / state.zoom;
      let dy = (event.clientY - interaction.startClientY) / state.zoom;
      if (event.shiftKey) {
        if (Math.abs(dx) > Math.abs(dy)) dy = 0;
        else dx = 0;
      }
      layer.x = interaction.startX + dx;
      layer.y = interaction.startY + dy;
      if (Math.abs(dx) > 0.3 || Math.abs(dy) > 0.3) interaction.changed = true;
      updateLayerVisual(layer);
      updateSelection();
      return;
    }

    if (interaction.type === "scale") {
      const start = interaction.startLayer;
      const handle = interaction.handle;
      const sx = handle.includes("e") ? 1 : -1;
      const sy = handle.includes("s") ? 1 : -1;
      const angle = start.rotation * Math.PI / 180;
      const cos = Math.cos(angle);
      const sin = Math.sin(angle);
      const centerX = start.x + start.w / 2;
      const centerY = start.y + start.h / 2;
      const oppositeLocalX = -sx * start.w / 2;
      const oppositeLocalY = -sy * start.h / 2;
      const oppositeX = centerX + cos * oppositeLocalX - sin * oppositeLocalY;
      const oppositeY = centerY + sin * oppositeLocalX + cos * oppositeLocalY;

      const point = clientToCanvas(event);
      const dx = point.x - oppositeX;
      const dy = point.y - oppositeY;
      const localX = cos * dx + sin * dy;
      const localY = -sin * dx + cos * dy;

      if (isTextLayer(layer)) {
        const diagonalX = sx * start.w;
        const diagonalY = sy * start.h;
        const denominator = diagonalX * diagonalX + diagonalY * diagonalY;
        let factor = denominator
          ? (localX * diagonalX + localY * diagonalY) / denominator
          : 1;
        const minFactor = Math.max(
          6 / start.fontSize,
          MIN_LAYER_SIZE / start.w,
          MIN_LAYER_SIZE / start.h
        );
        const maxFactor = Math.min(20, 400 / start.fontSize);
        factor = clamp(factor, minFactor, maxFactor);

        const newWidth = start.w * factor;
        const newHeight = start.h * factor;
        const halfLocalX = sx * newWidth / 2;
        const halfLocalY = sy * newHeight / 2;
        const newCenterX = oppositeX + cos * halfLocalX - sin * halfLocalY;
        const newCenterY = oppositeY + sin * halfLocalX + cos * halfLocalY;

        if (Math.abs(factor - 1) > 0.004) interaction.changed = true;
        layer.w = newWidth;
        layer.h = newHeight;
        layer.fontSize = clamp(start.fontSize * factor, 6, 400);
        layer.x = newCenterX - newWidth / 2;
        layer.y = newCenterY - newHeight / 2;
        updateLayerVisual(layer);
        updateSelection();
        showSelectionBadge(`字号 ${Math.round(layer.fontSize)}`);
        return;
      }

      let newWidth = Math.max(MIN_LAYER_SIZE, localX * sx);
      let newHeight = Math.max(MIN_LAYER_SIZE, localY * sy);

      if (event.shiftKey) {
        const ratio = start.w / start.h;
        if (newWidth / newHeight > ratio) newWidth = newHeight * ratio;
        else newHeight = newWidth / ratio;
        newWidth = Math.max(MIN_LAYER_SIZE, newWidth);
        newHeight = Math.max(MIN_LAYER_SIZE, newHeight);
      }

      const halfLocalX = sx * newWidth / 2;
      const halfLocalY = sy * newHeight / 2;
      const newCenterX = oppositeX + cos * halfLocalX - sin * halfLocalY;
      const newCenterY = oppositeY + sin * halfLocalX + cos * halfLocalY;

      if (Math.abs(newWidth - start.w) > 0.3 || Math.abs(newHeight - start.h) > 0.3) {
        interaction.changed = true;
      }

      layer.w = newWidth;
      layer.h = newHeight;
      layer.x = newCenterX - newWidth / 2;
      layer.y = newCenterY - newHeight / 2;
      updateLayerVisual(layer);
      updateSelection();
      showSelectionBadge(`${Math.round(layer.w)} × ${Math.round(layer.h)}`);
      return;
    }

    if (interaction.type === "rotate") {
      const point = clientToCanvas(event);
      const pointerAngle = Math.atan2(point.y - interaction.centerY, point.x - interaction.centerX) * 180 / Math.PI;
      let delta = pointerAngle - interaction.startPointerAngle;
      while (delta > 180) delta -= 360;
      while (delta < -180) delta += 360;
      let rotation = interaction.startRotation + delta;
      if (event.shiftKey) rotation = Math.round(rotation / 15) * 15;
      if (Math.abs(rotation - interaction.startRotation) > 0.1) interaction.changed = true;
      layer.rotation = rotation;
      updateLayerVisual(layer);
      updateSelection();
      showSelectionBadge(`${Math.round(layer.rotation)}°`);
    }
  }

  function onPointerUp() {
    const interaction = state.interaction;
    if (!interaction) return;
    state.interaction = null;
    document.body.classList.remove("is-dragging");
    document.body.style.cursor = "";
    el.selBadge.classList.remove("is-visible");

    if (interaction.changed) {
      pushHistory(interaction.before);
      scheduleSave();
    }
    updateSelection();
  }

  // ---------------------------------------------------------------------------
  // Preview
  // ---------------------------------------------------------------------------

  function openPreview() {
    if (!state.project || !state.project.pages.length) return;
    const index = state.project.pages.findIndex((page) => page.id === state.currentPageId);
    state.previewIndex = index < 0 ? 0 : index;
    el.previewModal.hidden = false;
    renderPreview();
    requestAnimationFrame(() => fitPreview());
  }

  function closePreview() {
    el.previewModal.hidden = true;
  }

  function previewGo(direction) {
    if (!state.project || state.project.pages.length < 2) return;
    const length = state.project.pages.length;
    state.previewIndex = (state.previewIndex + direction + length) % length;
    renderPreview();
    fitPreview();
  }

  function renderPreview() {
    const page = state.project && state.project.pages[state.previewIndex];
    if (!page) return;

    el.previewTitle.textContent = page.name;
    el.previewCounter.textContent = `第 ${state.previewIndex + 1} / ${state.project.pages.length} 页`;
    el.previewPrevBtn.disabled = state.project.pages.length < 2;
    el.previewNextBtn.disabled = state.project.pages.length < 2;
    el.previewCanvas.replaceChildren();
    el.previewCanvas.style.width = `${page.width}px`;
    el.previewCanvas.style.height = `${page.height}px`;

    let visibleCount = 0;
    page.layers.forEach((layer, index) => {
      if (layer.hidden) return;
      if (isTextLayer(layer)) {
        visibleCount += 1;
        el.previewCanvas.append(createTextLayerElement(layer, index + 1, true));
        return;
      }
      const asset = getAsset(layer.assetId);
      if (!asset) return;
      visibleCount += 1;
      el.previewCanvas.append(createLayerElement(layer, asset, index + 1, true));
    });

    if (visibleCount === 0) {
      const empty = document.createElement("div");
      empty.className = "preview-empty";
      empty.textContent = "空白页面";
      el.previewCanvas.append(empty);
    }
  }

  function fitPreview() {
    const page = state.project && state.project.pages[state.previewIndex];
    if (!page || el.previewModal.hidden) return;
    const width = el.previewStageWrap.clientWidth;
    const height = el.previewStageWrap.clientHeight;
    if (!width || !height) return;

    const scale = clamp(Math.min((width - 28) / page.width, (height - 28) / page.height, 1.5), 0.05, 1.5);
    el.previewHolder.style.width = `${page.width * scale}px`;
    el.previewHolder.style.height = `${page.height * scale}px`;
    el.previewCanvas.style.transform = `scale(${scale})`;
  }

  function wrapCanvasText(context, text, maxWidth) {
    const lines = [];
    const paragraphs = String(text || "").split(/\r?\n/);
    for (const paragraph of paragraphs) {
      if (!paragraph) {
        lines.push("");
        continue;
      }
      let line = "";
      for (const character of Array.from(paragraph)) {
        const candidate = line + character;
        if (line && context.measureText(candidate).width > maxWidth) {
          lines.push(line);
          line = character;
        } else {
          line = candidate;
        }
      }
      lines.push(line);
    }
    return lines;
  }

  function drawTextLayer(context, layer) {
    const lineHeight = layer.fontSize * layer.lineHeight;
    context.font = `${layer.fontWeight} ${layer.fontSize}px ${layer.fontFamily}`;
    context.fillStyle = layer.color;
    context.textAlign = "left";
    context.textBaseline = "top";
    const lines = wrapCanvasText(context, layer.text, Math.max(1, layer.w));
    const verticalOffset = Math.max(0, (lineHeight - layer.fontSize) / 2);
    lines.forEach((line, index) => {
      context.fillText(line, -layer.w / 2, -layer.h / 2 + verticalOffset + index * lineHeight);
    });
  }

  async function downloadPreviewPng() {
    const page = state.project && state.project.pages[state.previewIndex];
    if (!page) return;

    el.previewPngBtn.disabled = true;
    try {
      const visible = page.layers.filter((layer) => {
        if (layer.hidden) return false;
        return isTextLayer(layer) || Boolean(getAsset(layer.assetId));
      });
      const images = await Promise.all(visible.map((layer) => {
        if (isTextLayer(layer)) return null;
        return loadImage(getAsset(layer.assetId).dataUrl);
      }));
      const canvas = document.createElement("canvas");
      canvas.width = page.width;
      canvas.height = page.height;
      const context = canvas.getContext("2d");
      context.fillStyle = "#ffffff";
      context.fillRect(0, 0, canvas.width, canvas.height);
      context.imageSmoothingEnabled = true;
      context.imageSmoothingQuality = "high";

      visible.forEach((layer, index) => {
        context.save();
        context.translate(layer.x + layer.w / 2, layer.y + layer.h / 2);
        context.rotate(layer.rotation * Math.PI / 180);
        if (isTextLayer(layer)) {
          drawTextLayer(context, layer);
        } else {
          const image = images[index];
          context.drawImage(image, -layer.w / 2, -layer.h / 2, layer.w, layer.h);
        }
        context.restore();
      });

      const blob = await new Promise((resolve) => canvas.toBlob(resolve, "image/png"));
      if (!blob) throw new Error("无法生成图片");
      downloadBlob(blob, `${sanitizeFilename(page.name)}-${dateStamp()}.png`);
      toast("页面图片已导出", "success", 2200);
    } catch (error) {
      toast(`导出图片失败：${error.message || "未知错误"}`, "error", 3200);
    } finally {
      el.previewPngBtn.disabled = false;
    }
  }

  // ---------------------------------------------------------------------------
  // Import / export
  // ---------------------------------------------------------------------------

  function readFileAsText(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result || ""));
      reader.onerror = () => reject(reader.error || new Error("文件读取失败"));
      reader.readAsText(file);
    });
  }

  function exportProject() {
    if (!state.project) return;
    try {
      const payload = {
        format: `${APP_FORMAT}-project`,
        version: APP_VERSION,
        exportedAt: new Date().toISOString(),
        project: state.project
      };
      const json = JSON.stringify(payload);
      const blob = new Blob([json], { type: "application/json;charset=utf-8" });
      downloadBlob(blob, `画布工坊-项目-${dateStamp()}.json`);
      toast(`已导出完整项目，共 ${state.project.pages.length} 个页面、${state.project.assets.length} 张素材`, "success", 3000);
    } catch (error) {
      toast(`导出失败：${error.message || "未知错误"}`, "error", 3200);
    }
  }

  async function importProjectFile(file) {
    if (!file) return;
    try {
      const text = await readFileAsText(file);
      const parsed = JSON.parse(text);
      const normalized = normalizeProject(parsed.project || parsed);
      const confirmed = window.confirm(
        `导入这个完整项目？\n\n${normalized.pages.length} 个页面 · ${normalized.assets.length} 张素材\n当前项目会被替换，但素材库会随项目一起导入。`
      );
      if (!confirmed) return;

      state.project = normalized;
      state.currentPageId = normalized.pages[0].id;
      state.selectedLayerId = null;
      state.renamingPageId = null;
      state.undoStack = [];
      state.redoStack = [];
      state.zoom = 1;
      state.zoomMode = "manual";
      imageCache.clear();
      updatePageHash();
      renderAll();
      scheduleSave(0);
      toast("项目已导入并自动保存", "success", 2800);
    } catch (error) {
      toast(`导入失败：${error.message || "文件格式不正确"}`, "error", 3800);
    } finally {
      el.importInput.value = "";
    }
  }

  // ---------------------------------------------------------------------------
  // Help / keyboard
  // ---------------------------------------------------------------------------

  function updateStorageModeText() {
    if (Store.mode === "server") {
      el.storageModeText.textContent = "当前数据位置：本文件夹 data/project.json";
    } else if (Store.mode === "indexeddb") {
      el.storageModeText.textContent = "当前为浏览器存储模式；用「启动.bat」打开可保存到本文件夹";
    } else {
      el.storageModeText.textContent = "当前为浏览器存储模式；建议用「启动.bat」打开";
    }
  }

  function openHelp() {
    updateStorageModeText();
    el.helpModal.hidden = false;
  }

  function closeHelp() {
    el.helpModal.hidden = true;
  }

  function onKeyDown(event) {
    if (!el.previewModal.hidden) {
      if (event.key === "Escape") {
        event.preventDefault();
        closePreview();
      } else if (event.key === "ArrowLeft") {
        event.preventDefault();
        previewGo(-1);
      } else if (event.key === "ArrowRight") {
        event.preventDefault();
        previewGo(1);
      }
      return;
    }

    if (!el.helpModal.hidden) {
      if (event.key === "Escape") {
        event.preventDefault();
        closeHelp();
      }
      return;
    }

    if (isTyping()) return;

    const modifier = event.ctrlKey || event.metaKey;
    const key = event.key.toLowerCase();

    if (modifier && key === "z") {
      event.preventDefault();
      if (event.shiftKey) redo();
      else undo();
      return;
    }

    if (modifier && key === "y") {
      event.preventDefault();
      redo();
      return;
    }

    if (modifier && key === "s") {
      event.preventDefault();
      saveNow().then(() => toast("已保存", "success", 1400));
      return;
    }

    if (modifier && (event.key === "+" || event.key === "=")) {
      event.preventDefault();
      zoomStep(1);
      return;
    }

    if (modifier && event.key === "-") {
      event.preventDefault();
      zoomStep(-1);
      return;
    }

    if (modifier && event.key === "0") {
      event.preventDefault();
      fitCanvas();
      return;
    }

    if (event.key === "Delete" || event.key === "Backspace") {
      if (state.selectedLayerId) {
        event.preventDefault();
        deleteLayer(state.selectedLayerId);
      }
      return;
    }

    if (event.key === "Escape") {
      if (state.renamingPageId) {
        state.renamingPageId = null;
        renderPages();
      } else if (state.selectedLayerId) {
        state.selectedLayerId = null;
        renderLayers();
        updateSelection();
      }
    }
  }

  // ---------------------------------------------------------------------------
  // Events
  // ---------------------------------------------------------------------------

  function bindEvents() {
    el.undoBtn.addEventListener("click", undo);
    el.redoBtn.addEventListener("click", redo);
    el.addPageBtn.addEventListener("click", addPage);
    el.addTextBtn.addEventListener("click", addTextLayer);
    el.previewBtn.addEventListener("click", openPreview);
    el.exportBtn.addEventListener("click", exportProject);
    el.importBtn.addEventListener("click", () => el.importInput.click());
    el.helpBtn.addEventListener("click", openHelp);

    el.textFontSelect.addEventListener("change", () => {
      const value = el.textFontSelect.value;
      updateSelectedTextStyle((layer) => {
        layer.fontFamily = value;
      }, true);
    });
    el.fontSizeMinus.addEventListener("click", () => adjustTextFontSize(-2));
    el.fontSizePlus.addEventListener("click", () => adjustTextFontSize(2));
    el.fontSizeInput.addEventListener("change", () => {
      const layer = getSelectedLayer();
      if (!el.fontSizeInput.value.trim()) {
        if (isTextLayer(layer)) el.fontSizeInput.value = String(Math.round(layer.fontSize * 10) / 10);
        syncTextInspector();
        return;
      }
      applyTextFontSize(num(el.fontSizeInput.value, layer?.fontSize || 72));
    });
    el.fontSizeInput.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        el.fontSizeInput.blur();
      } else if (event.key === "Escape") {
        event.preventDefault();
        const layer = getSelectedLayer();
        if (isTextLayer(layer)) el.fontSizeInput.value = String(Math.round(layer.fontSize * 10) / 10);
        syncTextInspector();
        el.fontSizeInput.blur();
      }
    });
    el.textBoldBtn.addEventListener("click", () => {
      updateSelectedTextStyle((layer) => {
        layer.fontWeight = layer.fontWeight >= 600 ? 400 : 700;
      }, true);
    });
    el.textColorInput.addEventListener("change", () => {
      updateSelectedTextStyle((layer) => {
        layer.color = normalizeColor(el.textColorInput.value);
      });
    });

    el.uploadBtn.addEventListener("click", () => el.assetInput.click());
    el.emptyUploadBtn.addEventListener("click", () => el.assetInput.click());
    el.assetInput.addEventListener("change", (event) => {
      handleAssetFiles(event.target.files);
    });
    el.importInput.addEventListener("change", (event) => {
      importProjectFile(event.target.files && event.target.files[0]);
    });

    el.zoomOutBtn.addEventListener("click", () => zoomStep(-1));
    el.zoomInBtn.addEventListener("click", () => zoomStep(1));
    el.zoomValueBtn.addEventListener("click", () => setZoom(1, null, "manual"));
    el.fitBtn.addEventListener("click", fitCanvas);
    el.workspaceScroll.addEventListener("wheel", onWorkspaceWheel, { passive: false });

    el.canvas.addEventListener("pointerdown", onCanvasPointerDown);
    el.canvas.addEventListener("dblclick", (event) => {
      const node = event.target.closest(".canvas-layer.text-layer");
      if (!node) return;
      event.preventDefault();
      beginTextEditing(node.dataset.layerId, false);
    });
    el.selectionOverlay.addEventListener("pointerdown", onSelectionPointerDown);
    window.addEventListener("pointermove", onPointerMove, { passive: false });
    window.addEventListener("pointerup", onPointerUp);
    window.addEventListener("pointercancel", onPointerUp);

    el.previewCloseBtn.addEventListener("click", closePreview);
    el.previewPrevBtn.addEventListener("click", () => previewGo(-1));
    el.previewNextBtn.addEventListener("click", () => previewGo(1));
    el.previewPngBtn.addEventListener("click", downloadPreviewPng);
    el.previewModal.addEventListener("pointerdown", (event) => {
      if (event.target === el.previewModal) closePreview();
    });

    el.helpCloseBtn.addEventListener("click", closeHelp);
    el.helpDoneBtn.addEventListener("click", closeHelp);
    el.helpModal.addEventListener("pointerdown", (event) => {
      if (event.target === el.helpModal) closeHelp();
    });

    el.assetDropZone.addEventListener("dragenter", (event) => {
      if (!event.dataTransfer || !Array.from(event.dataTransfer.types || []).includes("Files")) return;
      event.preventDefault();
      dragFileDepth += 1;
      el.assetDropZone.classList.add("dragover");
    });

    el.assetDropZone.addEventListener("dragover", (event) => {
      if (!event.dataTransfer || !Array.from(event.dataTransfer.types || []).includes("Files")) return;
      event.preventDefault();
      event.dataTransfer.dropEffect = "copy";
      el.assetDropZone.classList.add("dragover");
    });

    el.assetDropZone.addEventListener("dragleave", (event) => {
      if (event.relatedTarget && el.assetDropZone.contains(event.relatedTarget)) return;
      dragFileDepth = 0;
      el.assetDropZone.classList.remove("dragover");
    });

    el.assetDropZone.addEventListener("drop", (event) => {
      if (!event.dataTransfer) return;
      event.preventDefault();
      event.stopPropagation();
      dragFileDepth = 0;
      el.assetDropZone.classList.remove("dragover");
      handleAssetFiles(event.dataTransfer.files);
    });

    window.addEventListener("dragover", (event) => {
      if (event.dataTransfer && Array.from(event.dataTransfer.types || []).includes("Files")) {
        event.preventDefault();
      }
    });

    window.addEventListener("drop", (event) => {
      if (!event.dataTransfer || !Array.from(event.dataTransfer.types || []).includes("Files")) return;
      event.preventDefault();
      if (!el.assetDropZone.contains(event.target)) {
        toast("请把图片拖到左侧素材库区域", "", 2400);
      }
    });

    window.addEventListener("resize", () => {
      if (!el.previewModal.hidden) fitPreview();
      if (state.zoomMode === "fit") fitCanvas();
    });

    window.addEventListener("hashchange", () => {
      if (!state.project) return;
      const id = pageFromHash();
      if (id && id !== state.currentPageId && state.project.pages.some((page) => page.id === id)) {
        switchPage(id);
      }
    });

    document.addEventListener("keydown", onKeyDown);
  }

  // ---------------------------------------------------------------------------
  // Init
  // ---------------------------------------------------------------------------

  async function init() {
    bindEvents();
    setSaveStatus("saving", "读取中…");

    state.storageMode = await Store.init();
    updateStorageModeText();

    let loaded = null;
    try {
      loaded = await Store.load();
    } catch (error) {
      toast(`读取项目失败：${error.message || "未知错误"}`, "error", 3600);
    }

    let createdNew = false;
    if (loaded && loaded.project) {
      try {
        state.project = normalizeProject(loaded.project);
        state.revision = num(loaded.revision, 0);
      } catch (error) {
        toast(`项目文件无法解析，已新建空白项目：${error.message}`, "error", 4200);
      }
    }

    if (!state.project) {
      state.project = createDefaultProject();
      state.revision = loaded ? num(loaded.revision, 0) : 0;
      state.currentPageId = state.project.pages[0].id;
      createdNew = true;
    } else {
      const hashedPage = pageFromHash();
      state.currentPageId = hashedPage && state.project.pages.some((page) => page.id === hashedPage)
        ? hashedPage
        : state.project.pages[0].id;
    }

    state.selectedLayerId = null;
    state.zoom = 1;
    state.zoomMode = "manual";
    state.initialized = true;
    el.selectionBox.classList.remove("is-visible");
    updatePageHash();
    renderAll();
    startSync();

    if (createdNew) {
      await saveNow();
    } else {
      setSaveStatus("saved", `已保存 ${formatTime((loaded && loaded.updatedAt) || Date.now())}`);
    }

    if (state.storageMode !== "server") {
      toast("当前为浏览器本地存储模式；建议用「启动.bat」打开，以便项目保存到本文件夹。", "", 4600);
    }
  }

  init().catch((error) => {
    console.error(error);
    setSaveStatus("error", "初始化失败");
    toast(`初始化失败：${error.message || "未知错误"}`, "error", 5000);
  });
})();
