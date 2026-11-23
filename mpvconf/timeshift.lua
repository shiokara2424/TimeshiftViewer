-- Timeshift Viewer: ライブ追従・巻き戻し・OSD（シークバーとボタン）・ペン書き込み
local mp = require 'mp'
local options = require 'mp.options'

local opts = {
  buffer = 600,          -- 保持する秒数
  pen_autoclear = true,  -- ライブに戻ったらペンの書き込みをすべて消す
}
options.read_options(opts, 'timeshift')

local live = true            -- ライブに追従している状態か
local show_until = 0         -- 操作パネルを表示しておく期限
local badge_until = 0        -- LIVE バッジを表示しておく期限
local message, message_until = nil, 0
local speed_by_us = false
local panel_hidden = false   -- Tab で操作パネルを隠している
local overlay = mp.create_osd_overlay('ass-events')

-- 連打用: 直前のシーク先と、連続して押した秒数の合計
local pending, pending_at = nil, 0
local acc, acc_at = 0, 0
local CHAIN_SEC = 1.0

local function now() return mp.get_time() end
local function flash(sec) show_until = math.max(show_until, now() + (sec or 3)); badge_until = show_until end
local pen  -- 下で定義（ペンモード中はメッセージで操作パネルを出さない）
local function msg(text)
  message = text; message_until = now() + 1.2
  if pen and pen.on then badge_until = math.max(badge_until, now() + 3) else flash() end
end

-- ------------------------------------------------------------
--  ペン
-- ------------------------------------------------------------
-- 色は ASS 形式（BBGGRR）
local PEN_COLORS = {
  { name = '赤',   c = '3C3CFF' },
  { name = '黄',   c = '00E6FF' },
  { name = '水色', c = 'FFD000' },
  { name = '緑',   c = '40E040' },
  { name = '紫',   c = 'F050B4' },
  { name = '白',   c = 'FFFFFF' },
}
local PEN_WIDTH = 0.0065      -- 線の太さ（映像の高さに対する割合）
pen = {
  on = false,       -- ペンモード
  color = 1,
  strokes = {},     -- { color = n, pts = { {u, v}, ... } }  座標は映像に対する 0〜1
  cur = nil,        -- 描いている途中の線
}

-- 画面上で映像が表示されている範囲（黒帯を除く）
local function video_rect()
  local d = mp.get_property_native('osd-dimensions')
  if not d or not d.w or d.w <= 0 then return nil end
  local x1, y1 = d.ml or 0, d.mt or 0
  local x2, y2 = d.w - (d.mr or 0), d.h - (d.mb or 0)
  if x2 - x1 < 2 or y2 - y1 < 2 then return nil end
  return x1, y1, x2, y2
end

local function to_video(x, y)
  local x1, y1, x2, y2 = video_rect()
  if not x1 then return nil end
  local u = math.min(1, math.max(0, (x - x1) / (x2 - x1)))
  local v = math.min(1, math.max(0, (y - y1) / (y2 - y1)))
  return u, v
end

local function pen_add_point(m)
  if not pen.cur or not m then return end
  local u, v = to_video(m.x, m.y)
  if not u then return end
  local pts = pen.cur.pts
  local last = pts[#pts]
  if last then
    local _, y1, _, y2 = video_rect()
    local tol = 1.5 / math.max(1, y2 - y1)  -- 1.5px 未満の移動は間引く
    if math.abs(u - last[1]) < tol and math.abs(v - last[2]) < tol then return end
  end
  pts[#pts + 1] = { u, v }
end

local function pen_begin(m)
  pen.cur = { color = pen.color, pts = {} }
  pen.strokes[#pen.strokes + 1] = pen.cur
  pen_add_point(m)
end

local function pen_end()
  if pen.cur and #pen.cur.pts == 0 then table.remove(pen.strokes) end
  pen.cur = nil
end

local function pen_clear(quiet)
  if #pen.strokes == 0 then return false end
  pen.strokes = {}
  pen.cur = nil
  if not quiet then msg('書き込みを消去しました') end
  return true
end

local function pen_set(on)
  pen.on = on
  if not on then pen_end() end
  msg(on and ('ペン: ' .. PEN_COLORS[pen.color].name .. '（P で終了）') or 'ペンを終了しました')
end

mp.register_script_message('ts-pen', function() pen_set(not pen.on) end)
mp.register_script_message('ts-pen-color', function(n)
  n = tonumber(n)
  if not n or not PEN_COLORS[n] then return end
  pen.color = n
  pen.on = true
  msg('ペン: ' .. PEN_COLORS[n].name)
end)
mp.register_script_message('ts-pen-undo', function()
  pen_end()
  if #pen.strokes > 0 then table.remove(pen.strokes); msg('1つ取り消しました') end
end)
mp.register_script_message('ts-pen-clear', function() pen_clear(false) end)
mp.register_script_message('ts-panel', function()
  panel_hidden = not panel_hidden
  msg(panel_hidden and '操作パネルを隠しました（Tab で表示）' or '操作パネルを表示します')
end)

-- ライブに戻ったときの自動消去
local function on_back_to_live(text)
  if opts.pen_autoclear and pen_clear(true) then text = text .. '（書き込みを消去）' end
  msg(text)
end

-- ------------------------------------------------------------
--  キャッシュとシーク
-- ------------------------------------------------------------
-- 最新側の連続したキャッシュ範囲
-- 戻り値: 範囲の先頭, 受信済みの最新時刻(表示用), シーク可能な終端
local function cache_range()
  local st = mp.get_property_native('demuxer-cache-state')
  if not st then return nil end
  local r = st['seekable-ranges']
  if not r or #r == 0 then return nil end
  local best = r[1]
  for _, x in ipairs(r) do if x['end'] > best['end'] then best = x end end
  local ct = mp.get_property_number('demuxer-cache-time')
  local e = best['end']
  if ct and ct > e then e = ct end
  return best.start, e, best['end'] - 0.05
end

local function fmt(sec, tenth)
  local neg = sec < -0.05
  sec = math.abs(sec)
  local m = math.floor(sec / 60)
  local s = sec - m * 60
  local str
  if tenth then str = string.format('%02d:%04.1f', m, s) else str = string.format('%02d:%02d', m, math.floor(s)) end
  return (neg and '-' or '') .. str
end

local function set_speed(v)
  if mp.get_property_number('speed', 1) ~= v then mp.set_property_number('speed', v) end
end

local function go_live()
  local _, e, se = cache_range()
  if not e then return end
  live = true
  pending = nil; acc = 0
  set_speed(1)
  mp.set_property_bool('pause', false)
  local pos = mp.get_property_number('time-pos') or 0
  if se > pos then mp.commandv('seek', tostring(se), 'absolute') end
  on_back_to_live('ライブに戻りました')
end

-- 絶対位置へシーク。実際のシーク先を返す（ライブに戻った場合は nil）
local function seek_abs(t, label)
  local s, e, se = cache_range()
  if not e then return nil end
  if t >= e - 0.6 and not mp.get_property_bool('pause') then go_live() return nil end
  if t > se then t = se end
  if t < s + 0.1 then t = s + 0.1; label = 'バッファの最古位置です' end
  live = false
  set_speed(1)
  mp.commandv('seek', tostring(t), 'absolute+exact')
  if label then msg(label) else flash() end
  return t
end

-- 相対シーク。連打時は前回のシーク先を基準に積み上げる
local function ts_seek(delta)
  delta = tonumber(delta) or 0
  local t = now()
  local paused = mp.get_property_bool('pause')
  local base
  if pending and t - pending_at < CHAIN_SEC then
    base = pending + (paused and 0 or (t - pending_at))
  else
    base = mp.get_property_number('time-pos')
    if not base then return end
  end
  if t - acc_at < CHAIN_SEC then acc = acc + delta else acc = delta end
  acc_at = t
  local label = nil
  if math.abs(delta) < 100000 then label = (acc > 0 and '+' or '') .. string.format('%g', acc) .. ' 秒' end
  local target = seek_abs(base + delta, label)
  if target then pending, pending_at = target, t else pending = nil end
end

local function toggle_pause()
  pending = nil
  mp.commandv('cycle', 'pause')
end

mp.register_script_message('ts-seek', ts_seek)
mp.register_script_message('ts-live', go_live)
mp.register_script_message('ts-pause', function() toggle_pause() end)

mp.observe_property('pause', 'bool', function(_, paused)
  if paused then live = false end
  flash()
end)

-- ------------------------------------------------------------
--  レイアウト
-- ------------------------------------------------------------
local PANEL_H = 120
local function layout()
  local w, h = mp.get_osd_size()
  if not w or w <= 0 then return nil end
  local L = { w = w, h = h, panel_y = h - PANEL_H }
  L.bar_x1, L.bar_x2, L.bar_y = 30, w - 230, h - 88
  -- ボタン行
  local defs = {
    { label = '« 10s', w = 84, act = function() ts_seek(-10) end },
    { label = '« 5s',  w = 76, act = function() ts_seek(-5) end },
    { label = 'play',  w = 64, act = toggle_pause },
    { label = '5s »',  w = 76, act = function() ts_seek(5) end },
    { label = '10s »', w = 84, act = function() ts_seek(10) end },
    { label = 'live',  w = 96, act = go_live, gap = 28 },
    { label = 'sim',   w = 84, act = function() mp.commandv('script-message', 'tsv-sim') end, gap = 28 },
  }
  local gap, bh = 10, 40
  local total = 0
  for i, d in ipairs(defs) do total = total + d.w + (i > 1 and (d.gap or gap) or 0) end
  local x = math.floor((w - total) / 2)
  local y1 = h - 58
  for i, d in ipairs(defs) do
    if i > 1 then x = x + (d.gap or gap) end
    d.x1, d.y1, d.x2, d.y2 = x, y1, x + d.w, y1 + bh
    x = d.x2
  end
  -- ペンのボタンは左端（パレットをその真上に出す。盤面に重ならない位置）
  local penb = { label = 'pen', w = 64, act = function() pen_set(not pen.on) end }
  penb.x1, penb.y1, penb.x2, penb.y2 = 30, y1, 30 + penb.w, y1 + bh
  table.insert(defs, 1, penb)
  L.buttons = defs

  -- ペンのパレット（ペンモード中だけ。ペンのボタンの真上に縦に並べる）
  if pen.on then
    local items = {}
    for i = 1, #PEN_COLORS do items[#items + 1] = { kind = 'color', n = i, act = function() pen.color = i end } end
    items[#items + 1] = { kind = 'undo',  label = '戻す',   act = function() mp.commandv('script-message', 'ts-pen-undo') end, gap = 18 }
    items[#items + 1] = { kind = 'clear', label = '全消し', act = function() pen_clear(false) end }
    items[#items + 1] = { kind = 'close', label = '終了',   act = function() pen_set(false) end, gap = 18 }
    local pw, ph, pg = 76, 46, 8
    local tot = 0
    for i, it in ipairs(items) do tot = tot + ph + (i > 1 and (it.gap or pg) or 0) end
    local px = math.max(8, penb.x1 + math.floor((penb.w - pw) / 2))
    local py = math.max(40, L.panel_y - 14 - tot)   -- 操作パネル（シークバー）のすぐ上から上へ積む
    for i, it in ipairs(items) do
      if i > 1 then py = py + (it.gap or pg) end
      it.x1, it.y1, it.x2, it.y2 = px, py, px + pw, py + ph
      py = it.y2
    end
    L.palette = items
    L.palette_box = { px - 8, items[1].y1 - 8, px + pw + 8, items[#items].y2 + 8 }
  end
  return L
end

local function hit_palette(L, mx, my)
  if not L.palette then return nil end
  for _, it in ipairs(L.palette) do
    if mx >= it.x1 and mx <= it.x2 and my >= it.y1 and my <= it.y2 then return it end
  end
  local b = L.palette_box
  if mx >= b[1] and mx <= b[3] and my >= b[2] and my <= b[4] then return { act = function() end } end  -- 隙間は無視
  return nil
end

local function panel_visible()
  if panel_hidden or pen.cur then return false end
  -- ペンモード中は、マウスを画面下に持っていったときだけ表示する（盤面の下側に書けるように）
  if pen.on then return now() < show_until end
  return mp.get_property_bool('pause') or not live or now() < show_until
end

local function hit(L, mx, my)
  for _, b in ipairs(L.buttons) do
    if mx >= b.x1 and mx <= b.x2 and my >= b.y1 and my <= b.y2 then return b end
  end
  return nil
end

mp.observe_property('mouse-pos', 'native', function(_, m)
  if pen.cur then pen_add_point(m) return end
  if pen.on then
    local L = layout()
    if L and m and m.y >= L.panel_y then flash(2.5) end
    return
  end
  flash(2.5)
end)

-- クリック（ボタン / シークバー / ペン）
mp.add_key_binding(nil, 'click', function(e)
  if e.event == 'up' then pen_end() return end
  if e.event ~= 'down' and e.event ~= 'press' then return end
  local L = layout()
  if not L then return end
  local m = mp.get_property_native('mouse-pos')
  if not m then return end

  local pi = hit_palette(L, m.x, m.y)
  if pi then
    if e.event == 'down' or e.event == 'press' then pi.act() end
    return
  end

  if m.y >= L.panel_y and panel_visible() then
    local b = hit(L, m.x, m.y)
    if b then b.act() return end
    if math.abs(m.y - L.bar_y) <= 16 and m.x >= L.bar_x1 - 10 and m.x <= L.bar_x2 + 10 then
      local s, e2 = cache_range()
      if not e2 then return end
      local frac = (m.x - L.bar_x1) / (L.bar_x2 - L.bar_x1)
      if frac >= 0.995 then go_live() return end
      pending = nil
      seek_abs(s + (e2 - s) * math.max(0, frac), nil)
    end
    return
  end

  if pen.on then
    if e.event == 'down' then pen_begin(m) end
    return
  end
  if m.y >= L.panel_y then flash() end
end, { complex = true })

-- ダブルクリック: 操作パネル上とペンモード中は無視（連打を全画面切り替えにしない）
mp.add_key_binding(nil, 'dblclick', function()
  if pen.on then return end
  local L = layout()
  local m = mp.get_property_native('mouse-pos')
  if L and m and m.y >= L.panel_y then return end
  mp.commandv('cycle', 'fullscreen')
end)

-- ------------------------------------------------------------
--  描画
-- ------------------------------------------------------------
local function rect(x1, y1, x2, y2, color, alpha)
  return string.format('{\\an7\\pos(0,0)\\bord0\\shad0\\1c&H%s&\\1a&H%s&\\p1}m %d %d l %d %d l %d %d l %d %d{\\p0}',
    color, alpha, x1, y1, x2, y1, x2, y2, x1, y2)
end
local function text_at(x, y, an, size, color, str)
  return string.format('{\\an%d\\pos(%d,%d)\\fs%d\\bord0\\shad0\\b1\\1c&H%s&}%s', an, x, y, size, color, str)
end

-- 線は「行って戻る」面積ゼロの図形にして、その縁取りで描く
local function draw_strokes(ev)
  if #pen.strokes == 0 then return end
  local x1, y1, x2, y2 = video_rect()
  if not x1 then return end
  local vw, vh = x2 - x1, y2 - y1
  local r = math.max(1, PEN_WIDTH * vh / 2)
  for _, s in ipairs(pen.strokes) do
    local pts = s.pts
    if #pts > 0 then
      local xy = {}
      for i, p in ipairs(pts) do xy[i] = string.format('%.1f %.1f', x1 + p[1] * vw, y1 + p[2] * vh) end
      local path
      if #xy == 1 then
        local p = pts[1]
        path = string.format('m %s l %.1f %.1f', xy[1], x1 + p[1] * vw + 0.5, y1 + p[2] * vh)
      else
        local back = {}
        for i = #xy - 1, 1, -1 do back[#back + 1] = xy[i] end
        path = 'm ' .. xy[1] .. ' l ' .. table.concat(xy, ' ', 2) .. ' ' .. table.concat(back, ' ')
      end
      local col = PEN_COLORS[s.color].c
      -- 下に黒い縁、上に色
      ev[#ev + 1] = string.format('{\\an7\\pos(0,0)\\shad0\\1a&HFF&\\3c&H000000&\\3a&H60&\\bord%.1f\\p1}%s{\\p0}', r + 2, path)
      ev[#ev + 1] = string.format('{\\an7\\pos(0,0)\\shad0\\1a&HFF&\\3c&H%s&\\3a&H00&\\bord%.1f\\p1}%s{\\p0}', col, r, path)
    end
  end
end

-- ------------------------------------------------------------
--  対戦の区切り（TimeshiftViewer 本体が映像から検知して送ってくる）
-- ------------------------------------------------------------
local utils = require 'mp.utils'
local matches = {}
mp.register_script_message('ts-matches', function(json)
  local ok, v = pcall(utils.parse_json, json)
  if ok and type(v) == 'table' then matches = v end
end)

local function ass_safe(s) return (tostring(s or ''):gsub('[{}\\]', '')) end

local function match_label(m)
  local n1, n2 = ass_safe(m.n1), ass_safe(m.n2)
  local s = string.format('第%d戦  %s', m.id, n1)
  if (m.w1 or -1) >= 0 then s = s .. string.format('  %d - %d  ', m.w1, m.w2) else s = s .. '  vs  ' end
  s = s .. n2
  if (m.set1 or -1) >= 0 then s = s .. string.format('   Set %d-%d', m.set1, m.set2) end
  if (m.e or -1) < 0 then s = s .. '   （対戦中）'
  elseif m.win == 0 then s = s .. '   → ' .. n1 .. ' の勝ち'
  elseif m.win == 1 then s = s .. '   → ' .. n2 .. ' の勝ち' end
  return s
end

function draw_matches(ev, L, s, e, pos)
  if #matches == 0 then return end
  local x1, x2, y = L.bar_x1, L.bar_x2, L.bar_y
  local span = math.max(0.001, e - s)
  local function X(t) return x1 + (x2 - x1) * math.min(1, math.max(0, (t - s) / span)) end
  -- マウスがシークバーの上にあれば、その位置の試合を表示する
  local mpos = mp.get_property_native('mouse-pos')
  local ht = nil
  if mpos and math.abs(mpos.y - y) <= 18 and mpos.x >= x1 and mpos.x <= x2 then
    ht = s + span * (mpos.x - x1) / (x2 - x1)
  end
  local cur, hov = nil, nil
  for _, m in ipairs(matches) do
    local ms = m.s or 0
    local me = ((m.e or -1) >= 0) and m.e or e
    if me >= s and ms <= e then
      local a, b = X(ms), X(me)
      local col = (m.id % 2 == 0) and '40B0F0' or 'F0C040'     -- BGR（橙 / 水色）
      ev[#ev + 1] = rect(a, y + 6, math.max(a + 3, b), y + 12, col, '10')
      ev[#ev + 1] = rect(a - 1, y - 12, a + 1, y + 12, 'FFFFFF', '30')
    end
    if pos >= ms and pos <= me then cur = m end
    if ht and ht >= ms and ht <= me then hov = m end
  end
  local show = hov or cur
  if show then
    ev[#ev + 1] = string.format('{\\an1\\pos(%d,%d)\\fs21\\bord3\\shad0\\1c&HFFFFFF&\\3c&H000000&}%s',
      x1, y - 16, match_label(show))
    if show.tsumo and show.tsumo ~= '' then
      ev[#ev + 1] = string.format('{\\an3\\pos(%d,%d)\\fs17\\bord3\\shad0\\1c&HC8C8C8&\\3c&H000000&}ツモ %s（%d手読取）',
        x2, y - 16, ass_safe(show.tsumo), show.hands or 0)
    end
  end
end

local function draw()
  local L = layout()
  if not L then return end
  local w, h = L.w, L.h
  local pos = mp.get_property_number('time-pos')
  local s, e = cache_range()
  local paused = mp.get_property_bool('pause')
  local t = now()
  local ev = {}

  draw_strokes(ev)

  -- ペンのパレット（右端）
  if pen.on and L.palette then
    local m = mp.get_property_native('mouse-pos')
    local hov = m and hit_palette(L, m.x, m.y)
    local b = L.palette_box
    ev[#ev + 1] = rect(b[1], b[2], b[3], b[4], '101010', '50')
    for _, it in ipairs(L.palette) do
      local hot = (hov == it)
      if it.kind == 'color' then
        local sel = (pen.color == it.n)
        if sel then ev[#ev + 1] = rect(it.x1 - 4, it.y1 - 4, it.x2 + 4, it.y2 + 4, 'FFFFFF', '00') end
        ev[#ev + 1] = rect(it.x1, it.y1, it.x2, it.y2, hot and '505050' or '202020', '00')
        ev[#ev + 1] = rect(it.x1 + 6, it.y1 + 6, it.x2 - 6, it.y2 - 6, PEN_COLORS[it.n].c, '00')
      else
        local fill = hot and (it.kind == 'close' and '3A3ADD' or '606060') or '303030'
        ev[#ev + 1] = rect(it.x1, it.y1, it.x2, it.y2, fill, '10')
        ev[#ev + 1] = text_at((it.x1 + it.x2) / 2, (it.y1 + it.y2) / 2, 5, 22, 'FFFFFF', it.label)
      end
    end
    ev[#ev + 1] = text_at((b[1] + b[3]) / 2, b[2] - 8, 2, 20, 'FFFFFF', '✎ ペン')
  end

  if not pos or not e then
    ev[#ev + 1] = '{\\an7\\pos(24,20)\\fs30\\bord2\\1c&HFFFFFF&\\3c&H000000&}キャプチャ開始中...'
  else
    local behind = e - pos
    -- 左上のバッジ
    local text, col
    if paused then text, col = '❚❚ 一時停止   ' .. fmt(-behind, true), '555555'
    elseif live then text, col = '● LIVE', '4A3BFF'
    else text, col = '◀◀ 巻き戻し再生中   ' .. fmt(-behind, true), 'DF6C2D' end
    if paused or not live or t < badge_until then
      ev[#ev + 1] = string.format('{\\an7\\pos(24,20)\\fs28\\b1\\bord7\\shad0\\1c&HFFFFFF&\\3c&H%s&}%s', col, text)
    end
    -- メッセージ
    if message and t < message_until then
      ev[#ev + 1] = string.format('{\\an8\\pos(%d,22)\\fs28\\bord6\\shad0\\1c&HFFFFFF&\\3c&H202020&\\3a&H40&}%s', w / 2, message)
    end
    -- 下部の操作パネル
    if panel_visible() then
      local span = math.max(0.001, e - s)
      local frac = math.min(1, math.max(0, (pos - s) / span))
      local x1, x2, y = L.bar_x1, L.bar_x2, L.bar_y
      local fx = x1 + (x2 - x1) * frac
      ev[#ev + 1] = rect(0, L.panel_y, w, h, '000000', '40')
      ev[#ev + 1] = rect(x1, y - 3, x2, y + 3, 'FFFFFF', 'A0')
      ev[#ev + 1] = rect(x1, y - 3, fx, y + 3, live and '4A3BFF' or 'DF6C2D', '00')
      draw_matches(ev, L, s, e, pos)
      ev[#ev + 1] = rect(fx - 4, y - 10, fx + 4, y + 10, 'FFFFFF', '00')
      local label = (live and not paused) and ('LIVE  /  ' .. fmt(span)) or (fmt(-behind, true) .. '  /  ' .. fmt(span))
      ev[#ev + 1] = string.format('{\\an6\\pos(%d,%d)\\fs24\\bord0\\shad0\\1c&HFFFFFF&}%s', w - 24, y, label)

      -- ボタン
      local m = mp.get_property_native('mouse-pos')
      local hov = m and hit(L, m.x, m.y)
      for _, b in ipairs(L.buttons) do
        local fill, alpha = (hov == b) and '606060' or '303030', '30'
        local lbl = b.label
        if b.label == 'play' then lbl = paused and '▶' or '❚❚' end
        if b.label == 'sim' then lbl = 'シミュ' end
        if b.label == 'pen' then
          lbl = '✎'
          if pen.on then fill, alpha = PEN_COLORS[pen.color].c, '00' end
        end
        if b.label == 'live' then
          lbl = '● LIVE'
          if live and not paused then fill, alpha = '4A3BFF', '00' else fill, alpha = (hov == b) and '4A3BFF' or '303030', (hov == b) and '20' or '30' end
        end
        ev[#ev + 1] = rect(b.x1, b.y1, b.x2, b.y2, fill, alpha)
        local tc = (b.label == 'pen' and pen.on) and '000000' or 'FFFFFF'
        ev[#ev + 1] = text_at((b.x1 + b.x2) / 2, (b.y1 + b.y2) / 2, 5, 22, tc, lbl)
      end
    end
  end
  overlay.res_x, overlay.res_y = w, h
  overlay.data = table.concat(ev, '\n')
  overlay:update()
end

mp.add_periodic_timer(0.05, function()
  local pos = mp.get_property_number('time-pos')
  local s, e, se = cache_range()
  if pos and e then
    local behind = e - pos
    if mp.get_property_bool('pause') then
      -- 一時停止中も古いデータは捨てる（保持時間を超えたら位置を前へ送る）
      if behind > opts.buffer + 1 then mp.commandv('seek', tostring(math.min(e - opts.buffer, se)), 'absolute') end
    else
      local chaining = pending and (now() - pending_at < CHAIN_SEC)
      if not live and not chaining and behind < 0.8 then live = true; on_back_to_live('ライブに追いつきました') end
      if live then
        -- ライブ時は遅れが溜まらないよう少しだけ早送りして追従
        if behind > 1.2 then set_speed(1.08); speed_by_us = true
        elseif behind < 0.45 and speed_by_us then set_speed(1); speed_by_us = false end
      end
    end
  end
  draw()
end)

flash(4)
