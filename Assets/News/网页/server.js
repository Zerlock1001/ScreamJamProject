"use strict";

const http = require("http");
const fs = require("fs");
const fsp = fs.promises;
const path = require("path");
const { spawn } = require("child_process");

const ROOT = __dirname;
const DATA_DIR = path.join(ROOT, "data");
const PROJECT_FILE = path.join(DATA_DIR, "project.json");
const MAX_BODY = 300 * 1024 * 1024;
const HOST = "127.0.0.1";

let defaultPort = Number(process.env.PORT) || 5188;
const shouldOpen = process.argv.includes("--open");

const store = {
  revision: 0,
  updatedAt: 0,
  loaded: false
};

const MIME = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".jpeg": "image/jpeg",
  ".webp": "image/webp",
  ".gif": "image/gif",
  ".avif": "image/avif",
  ".ico": "image/x-icon",
  ".txt": "text/plain; charset=utf-8",
  ".md": "text/markdown; charset=utf-8",
  ".woff": "font/woff",
  ".woff2": "font/woff2"
};

async function loadStoreMeta() {
  try {
    const raw = await fsp.readFile(PROJECT_FILE, "utf8");
    const parsed = JSON.parse(raw);
    store.revision = Number.isFinite(parsed.revision) ? parsed.revision : 0;
    store.updatedAt = Number.isFinite(parsed.updatedAt) ? parsed.updatedAt : 0;
  } catch (error) {
    if (error.code !== "ENOENT") {
      console.error("读取项目文件失败：", error.message);
    }
    store.revision = 0;
    store.updatedAt = 0;
  } finally {
    store.loaded = true;
  }
}

function sendJson(res, status, payload) {
  const body = Buffer.from(JSON.stringify(payload), "utf8");
  res.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Content-Length": body.length,
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff"
  });
  res.end(body);
}

function readRequestBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let total = 0;
    let settled = false;

    req.on("data", (chunk) => {
      if (settled) return;
      total += chunk.length;
      if (total > MAX_BODY) {
        settled = true;
        const error = new Error("项目文件过大");
        error.statusCode = 413;
        reject(error);
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });

    req.on("end", () => {
      if (settled) return;
      settled = true;
      resolve(Buffer.concat(chunks));
    });

    req.on("error", (error) => {
      if (settled) return;
      settled = true;
      reject(error);
    });
  });
}

async function handleApi(req, res, pathname) {
  if (pathname === "/api/meta" && req.method === "GET") {
    if (!store.loaded) await loadStoreMeta();
    sendJson(res, 200, {
      revision: store.revision,
      updatedAt: store.updatedAt
    });
    return true;
  }

  if (pathname === "/api/project" && req.method === "GET") {
    try {
      const raw = await fsp.readFile(PROJECT_FILE, "utf8");
      const parsed = JSON.parse(raw);
      store.revision = Number.isFinite(parsed.revision) ? parsed.revision : store.revision;
      store.updatedAt = Number.isFinite(parsed.updatedAt) ? parsed.updatedAt : store.updatedAt;
      sendJson(res, 200, {
        revision: store.revision,
        updatedAt: store.updatedAt,
        project: parsed.project || null
      });
    } catch (error) {
      if (error.code === "ENOENT") {
        sendJson(res, 200, { revision: 0, updatedAt: 0, project: null });
      } else {
        console.error("读取项目失败：", error.message);
        sendJson(res, 500, { error: "无法读取项目文件" });
      }
    }
    return true;
  }

  if (pathname === "/api/project" && req.method === "PUT") {
    try {
      const raw = await readRequestBody(req);
      const parsed = JSON.parse(raw.toString("utf8"));
      const project = parsed && parsed.project;
      if (!project || typeof project !== "object" || !Array.isArray(project.pages) || project.pages.length === 0) {
        sendJson(res, 400, { error: "项目内容不完整" });
        return true;
      }

      const updatedAt = Date.now();
      const revision = store.revision + 1;
      project.updatedAt = updatedAt;

      await fsp.mkdir(DATA_DIR, { recursive: true });
      const tempFile = path.join(DATA_DIR, `.project-${process.pid}-${Date.now()}.tmp`);
      const payload = JSON.stringify({
        app: "huabu-studio",
        revision,
        updatedAt,
        project
      });
      await fsp.writeFile(tempFile, payload, "utf8");
      await fsp.rename(tempFile, PROJECT_FILE);

      store.revision = revision;
      store.updatedAt = updatedAt;
      sendJson(res, 200, { ok: true, revision, updatedAt });
    } catch (error) {
      console.error("保存项目失败：", error.message);
      const status = error.statusCode || (error instanceof SyntaxError ? 400 : 500);
      sendJson(res, status, { error: status === 400 ? "项目 JSON 格式错误" : "保存失败" });
    }
    return true;
  }

  if (pathname.startsWith("/api/")) {
    sendJson(res, 404, { error: "接口不存在" });
    return true;
  }

  return false;
}

function resolveStaticPath(pathname) {
  let decoded;
  try {
    decoded = decodeURIComponent(pathname);
  } catch (_) {
    return null;
  }

  if (decoded === "/") decoded = "/index.html";
  if (decoded === "/data" || decoded.startsWith("/data/")) return null;

  const target = path.resolve(ROOT, `.${decoded}`);
  const rootWithSeparator = ROOT.endsWith(path.sep) ? ROOT : ROOT + path.sep;
  const normalizedRoot = process.platform === "win32" ? rootWithSeparator.toLowerCase() : rootWithSeparator;
  const normalizedTarget = process.platform === "win32" ? target.toLowerCase() : target;

  if (normalizedTarget !== (process.platform === "win32" ? ROOT.toLowerCase() : ROOT) &&
      !normalizedTarget.startsWith(normalizedRoot)) {
    return null;
  }
  return target;
}

async function serveStatic(req, res, pathname) {
  if (req.method !== "GET" && req.method !== "HEAD") {
    res.writeHead(405, { Allow: "GET, HEAD" });
    res.end("Method Not Allowed");
    return;
  }

  const filePath = resolveStaticPath(pathname);
  if (!filePath) {
    res.writeHead(403, { "Content-Type": "text/plain; charset=utf-8" });
    res.end("Forbidden");
    return;
  }

  try {
    let stat = await fsp.stat(filePath);
    let finalPath = filePath;
    if (stat.isDirectory()) {
      finalPath = path.join(filePath, "index.html");
      stat = await fsp.stat(finalPath);
    }

    res.writeHead(200, {
      "Content-Type": MIME[path.extname(finalPath).toLowerCase()] || "application/octet-stream",
      "Content-Length": stat.size,
      "Cache-Control": "no-cache",
      "X-Content-Type-Options": "nosniff",
      "Referrer-Policy": "no-referrer"
    });

    if (req.method === "HEAD") {
      res.end();
      return;
    }

    const stream = fs.createReadStream(finalPath);
    stream.on("error", (error) => {
      console.error("读取静态文件失败：", error.message);
      if (!res.headersSent) res.writeHead(500);
      res.end();
    });
    stream.pipe(res);
  } catch (error) {
    if (error.code === "ENOENT" || error.code === "ENOTDIR") {
      res.writeHead(404, { "Content-Type": "text/html; charset=utf-8" });
      res.end("<!doctype html><meta charset=\"utf-8\"><title>404</title><body style=\"font-family:sans-serif;padding:40px\"><h1>404</h1><p>文件不存在。</p></body>");
    } else {
      console.error("静态文件错误：", error.message);
      res.writeHead(500, { "Content-Type": "text/plain; charset=utf-8" });
      res.end("Internal Server Error");
    }
  }
}

const server = http.createServer(async (req, res) => {
  try {
    const requestUrl = new URL(req.url, `http://${req.headers.host || HOST}`);
    const pathname = requestUrl.pathname;

    if (pathname.startsWith("/api/")) {
      const handled = await handleApi(req, res, pathname);
      if (!handled) sendJson(res, 404, { error: "接口不存在" });
      return;
    }

    await serveStatic(req, res, pathname);
  } catch (error) {
    console.error("请求处理失败：", error);
    if (!res.headersSent) {
      res.writeHead(500, { "Content-Type": "text/plain; charset=utf-8" });
    }
    res.end("Internal Server Error");
  }
});

function openBrowser(url) {
  try {
    if (process.platform === "win32") {
      spawn("cmd", ["/c", "start", "", url], {
        detached: true,
        stdio: "ignore",
        windowsHide: true
      }).unref();
    } else if (process.platform === "darwin") {
      spawn("open", [url], { detached: true, stdio: "ignore" }).unref();
    } else {
      spawn("xdg-open", [url], { detached: true, stdio: "ignore" }).unref();
    }
  } catch (error) {
    console.error("自动打开浏览器失败，请手动访问地址。", error.message);
  }
}

let currentPort = defaultPort;
let attempts = 0;

function listen() {
  server.once("error", onListenError);
  server.listen(currentPort, HOST, () => {
    server.off("error", onListenError);
    const url = `http://${HOST}:${currentPort}/`;
    console.log("");
    console.log("  画布工坊 · 轻量排版编辑器");
    console.log(`  地址：${url}`);
    console.log("  数据：本文件夹中的 data/project.json");
    console.log("  关闭此窗口或按 Ctrl+C 即可停止。");
    console.log("");
    if (shouldOpen) openBrowser(url);
  });
}

function onListenError(error) {
  if (error.code === "EADDRINUSE" && attempts < 20) {
    attempts += 1;
    currentPort += 1;
    listen();
    return;
  }
  console.error("启动本地服务失败：", error.message);
  process.exitCode = 1;
}

process.on("SIGINT", () => {
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 1200);
});

loadStoreMeta()
  .catch(() => {})
  .finally(listen);
