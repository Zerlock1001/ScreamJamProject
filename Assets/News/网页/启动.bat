@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
title 画布工坊 - 本地编辑器

where node >nul 2>nul
if errorlevel 1 (
  echo.
  echo  没有找到 Node.js。
  echo  将直接用浏览器打开 index.html。
  echo  此模式下数据保存在浏览器内部，建议安装 Node.js 后使用 启动.bat。
  echo.
  start "" "%~dp0index.html"
  pause
  exit /b 0
)

node server.js --open
if errorlevel 1 (
  echo.
  echo  本地服务已停止或启动失败。
  pause
)
