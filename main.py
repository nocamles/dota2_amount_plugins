import sys
import threading
import logging
import json
import os
import time
import subprocess
import keyboard
from PyQt6.QtWidgets import (QApplication, QWidget, QLabel, QHBoxLayout, 
                             QSystemTrayIcon, QMenu, QGraphicsDropShadowEffect,
                             QDialog, QVBoxLayout, QPushButton, QMessageBox, QKeySequenceEdit,
                             QFrame)
from PyQt6.QtGui import QPixmap, QFont, QColor, QIcon, QAction, QKeySequence
from PyQt6.QtCore import Qt, pyqtSignal, QObject, QThread
from flask import Flask, request

# 关闭 Flask 控制台日志
log = logging.getLogger('werkzeug')
log.setLevel(logging.ERROR)

# --- 全局常量 ---
PORT = 3000
CONFIG_FILE = 'overlay_config.json'
DOTA_EXE = "dota2.exe"
CREATE_NO_WINDOW = 0x08000000

# --- 配置文件逻辑 ---
def load_config():
    if os.path.exists(CONFIG_FILE):
        try:
            with open(CONFIG_FILE, 'r', encoding='utf-8') as f:
                return json.load(f)
        except:
            pass
    return {"x": 200, "y": 60, "locked": True, "hotkey": "Ctrl+Alt+F10"}

def save_config(x, y, locked, hotkey):
    try:
        with open(CONFIG_FILE, 'w', encoding='utf-8') as f:
            json.dump({"x": x, "y": y, "locked": locked, "hotkey": hotkey}, f)
    except:
        pass

# --- 进程监控线程 ---
class ProcessMonitorThread(QThread):
    status_changed = pyqtSignal(bool)

    def run(self):
        is_running = False
        while True:
            try:
                result = subprocess.run(['tasklist', '/FI', f'IMAGENAME eq {DOTA_EXE}'],
                    capture_output=True, text=True, creationflags=CREATE_NO_WINDOW)
                current_status = DOTA_EXE.lower() in result.stdout.lower()
                
                if current_status != is_running:
                    is_running = current_status
                    self.status_changed.emit(is_running)
            except Exception:
                pass
            time.sleep(3) 

# --- 统一信号管理 ---
class WorkerSignals(QObject):
    update_net_worth = pyqtSignal(int)
    toggle_lock = pyqtSignal()
    update_match_status = pyqtSignal(bool) # <--- 新增信号：通知UI是否在对局中

# --- 组合快捷键捕获弹窗 ---
class HotkeyDialog(QDialog):
    def __init__(self, current_hotkey, parent=None):
        super().__init__(parent)
        self.setWindowTitle("设置全局快捷键")
        self.setFixedSize(300, 150)
        self.selected_hotkey = current_hotkey
        
        layout = QVBoxLayout()
        layout.addWidget(QLabel("请在下方输入框内按下任意组合键\n(例如 Ctrl + Alt + F10)："))
        
        self.key_edit = QKeySequenceEdit(self)
        self.key_edit.setKeySequence(QKeySequence(current_hotkey))
        layout.addWidget(self.key_edit)
        
        save_btn = QPushButton("保存并生效")
        save_btn.setStyleSheet("padding: 8px; background-color: #4CAF50; color: white; font-weight: bold; border-radius: 4px;")
        save_btn.clicked.connect(self.save_and_close)
        layout.addWidget(save_btn)
        
        self.setLayout(layout)

    def save_and_close(self):
        seq_str = self.key_edit.keySequence().toString()
        if not seq_str:
            QMessageBox.warning(self, "警告", "快捷键不能为空！")
            return
        
        self.selected_hotkey = seq_str.split(',')[0].strip()
        self.selected_hotkey = self.selected_hotkey.replace("Meta", "windows")
        self.accept()

# --- 主悬浮窗 UI ---
class NetWorthOverlay(QWidget):
    def __init__(self, signals):
        super().__init__()
        self.signals = signals
        self.config = load_config()
        self.is_locked = self.config.get("locked", True)
        self.hotkey = self.config.get("hotkey", "Ctrl+Alt+F10")
        
        # 两个核心状态判定
        self.dota_is_running = False  # 游戏进程是否启动
        self.in_match = False         # 玩家是否真正在打比赛/试玩

        self.current_hotkey_hook = None
        
        self.init_ui()
        self.init_tray()
        self.register_hotkey()
        
        self.signals.update_net_worth.connect(self.update_label)
        self.signals.toggle_lock.connect(self.toggle_lock_state)
        self.signals.update_match_status.connect(self.handle_match_status) # 绑定对局状态信号

    def init_ui(self):
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground)

        self.bg_frame = QFrame(self)
        self.bg_frame.setObjectName("BgFrame")

        inner_layout = QHBoxLayout(self.bg_frame)
        inner_layout.setContentsMargins(10, 5, 10, 5)
        inner_layout.setAlignment(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter)

        self.icon_label = QLabel()
        pixmap = QPixmap('icon.png')
        if not pixmap.isNull():
            pixmap = pixmap.scaled(24, 24, Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation)
            self.icon_label.setPixmap(pixmap)
        inner_layout.addWidget(self.icon_label)

        self.text_label = QLabel("0")
        font = QFont("Arial", 16, QFont.Weight.Bold)
        self.text_label.setFont(font)
        self.text_label.setStyleSheet("color: #F8E8B9;")
        self.text_label.setMinimumWidth(80) 
        self.text_label.setAlignment(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter)

        shadow = QGraphicsDropShadowEffect(self)
        shadow.setBlurRadius(3)            
        shadow.setXOffset(2)               
        shadow.setYOffset(2)               
        shadow.setColor(QColor(0, 0, 0, 255)) 
        self.text_label.setGraphicsEffect(shadow)
        
        inner_layout.addWidget(self.text_label)

        main_layout = QVBoxLayout(self)
        main_layout.setContentsMargins(0, 0, 0, 0)
        main_layout.addWidget(self.bg_frame)
        self.setLayout(main_layout)
        
        self.setFixedSize(140, 42)
        self.move(self.config.get("x", 200), self.config.get("y", 60))
        
        self.apply_lock_state()
        self.hide()

    def init_tray(self):
        self.tray_icon = QSystemTrayIcon(self)
        self.tray_icon.setIcon(QIcon('icon.png'))
        self.tray_menu = QMenu()
        
        self.tray_action_hotkey = QAction(f"修改快捷键 (当前: {self.hotkey})", self)
        self.tray_action_hotkey.triggered.connect(self.open_hotkey_dialog)
        self.tray_menu.addAction(self.tray_action_hotkey)
        self.tray_menu.addSeparator()
        self.tray_action_quit = QAction("退出插件", self)
        self.tray_action_quit.triggered.connect(self.quit_app)
        self.tray_menu.addAction(self.tray_action_quit)
        
        self.tray_icon.setContextMenu(self.tray_menu)
        self.tray_icon.show()

    def register_hotkey(self):
        if self.current_hotkey_hook is not None:
            try: keyboard.remove_hotkey(self.current_hotkey_hook)
            except: pass
        try:
            self.current_hotkey_hook = keyboard.add_hotkey(self.hotkey, lambda: self.signals.toggle_lock.emit())
        except:
            QMessageBox.warning(None, "快捷键注册失败", f"无法绑定快捷键 {self.hotkey}，请尝试更换。")

    def open_hotkey_dialog(self):
        dialog = HotkeyDialog(self.hotkey)
        if dialog.exec():
            self.hotkey = dialog.selected_hotkey
            self.tray_action_hotkey.setText(f"修改快捷键 (当前: {self.hotkey})")
            self.register_hotkey()
            save_config(self.x(), self.y(), self.is_locked, self.hotkey)

    def handle_dota_status(self, is_running):
        self.dota_is_running = is_running
        if not self.dota_is_running:
            self.is_locked = True
        self.apply_lock_state()

    def handle_match_status(self, in_match):
        # 接收到后台 GSI 发来的比赛状态变化
        if self.in_match != in_match:
            self.in_match = in_match
            self.apply_lock_state()

    def toggle_lock_state(self):
        if not self.dota_is_running:
            return 
        self.is_locked = not self.is_locked
        self.apply_lock_state()

    def apply_lock_state(self):
        if self.is_locked:
            self.setWindowFlags(
                Qt.WindowType.FramelessWindowHint | Qt.WindowType.WindowStaysOnTopHint | 
                Qt.WindowType.WindowTransparentForInput | Qt.WindowType.Tool  
            )
            self.bg_frame.setStyleSheet("") 
            self.setCursor(Qt.CursorShape.ArrowCursor)

            # --- 核心显示逻辑 ---
            # 只有当：游戏开着 + 人在对局里，才显示数字！
            if self.dota_is_running and self.in_match:
                self.show()
            else:
                self.hide()
        else:
            self.setWindowFlags(
                Qt.WindowType.FramelessWindowHint | Qt.WindowType.WindowStaysOnTopHint | Qt.WindowType.Tool  
            )
            self.bg_frame.setStyleSheet("""
                #BgFrame { 
                    background-color: rgba(20, 40, 20, 200); 
                    border: 2px dashed #4CAF50; 
                    border-radius: 8px; 
                }
            """)
            self.setCursor(Qt.CursorShape.SizeAllCursor)
            
            # --- 核心排版逻辑 ---
            # 只要解锁了（不管是不是在打游戏，只要游戏开了就行），强制显示出来，方便玩家在主菜单拖动排版
            if self.dota_is_running:
                self.show()
            else:
                self.hide()

        save_config(self.x(), self.y(), self.is_locked, self.hotkey)

    # 鼠标拖拽逻辑
    def mousePressEvent(self, event):
        if not self.is_locked and event.button() == Qt.MouseButton.LeftButton:
            self.drag_pos = event.globalPosition().toPoint() - self.frameGeometry().topLeft()
            event.accept()

    def mouseMoveEvent(self, event):
        if not self.is_locked and event.buttons() == Qt.MouseButton.LeftButton:
            self.move(event.globalPosition().toPoint() - self.drag_pos)
            event.accept()

    def mouseReleaseEvent(self, event):
        if not self.is_locked and event.button() == Qt.MouseButton.LeftButton:
            save_config(self.x(), self.y(), self.is_locked, self.hotkey)
            event.accept()

    def update_label(self, total_net_worth):
        self.text_label.setText(f"{total_net_worth:,}")

    def quit_app(self):
        if self.current_hotkey_hook is not None:
            try: keyboard.remove_hotkey(self.current_hotkey_hook)
            except: pass
        QApplication.instance().quit()

# --- 获取物品价格逻辑 ---
def get_item_prices():
    try:
        with open('item_price.json', 'r', encoding='utf-8') as file:
            data = json.load(file)
        prices = {}
        for key, value in data.items():
            if 'cost' in value:
                prices[key] = value['cost']
        return prices
    except:
        return {}

# --- Flask GSI 接收服务端 ---
def run_gsi_server(signals, item_prices):
    app = Flask(__name__)
    
    # --- 终极防御：状态缓存池 (解决 Dota2 GSI 增量更新导致数据暴跌的 Bug) ---
    gsi_cache = {
        'gold': 0,
        'items': {}, 
        'shard': False,
        'scepter_buff': False
    }

    @app.route('/', methods=['POST'])
    def handle_gsi():
        data = request.get_json()
        if not data: return "OK", 200

        # 1. 判定是否在对局中：只要数据包含玩家、英雄、物品任意一项，就是对局中
        # 在主菜单时，GSI 只会发一个孤零零的 {"provider": {...}}
        current_in_match = any(k in data and data[k] is not None for k in['player', 'hero', 'items', 'map'])
        
        if not current_in_match:
            # 退出对局或在主菜单，重置缓存，并通知 UI 隐藏
            gsi_cache['gold'] = 0
            gsi_cache['items'].clear()
            gsi_cache['shard'] = False
            gsi_cache['scepter_buff'] = False
            signals.update_match_status.emit(False)
            return "OK", 200

        # 在对局中，通知 UI 显示
        signals.update_match_status.emit(True)

        # 2. 将增量更新的数据合并到缓存池中
        if 'player' in data and data['player'] is not None:
            if 'gold' in data['player']:
                gsi_cache['gold'] = data['player']['gold']

        if 'items' in data and data['items'] is not None:
            for i in range(9):
                slot_name = f"slot{i}"
                if slot_name in data['items']:
                    item_data = data['items'][slot_name]
                    # 有装备
                    if item_data is not None and 'name' in item_data:
                        gsi_cache['items'][slot_name] = item_data['name']
                    # 装备被吃掉、卖掉或者放入储藏处，格子变空了
                    elif item_data is None or item_data.get('name') == 'empty':
                        gsi_cache['items'][slot_name] = "empty"

        if 'hero' in data and data['hero'] is not None:
            if 'aghanims_shard' in data['hero']:
                gsi_cache['shard'] = data['hero']['aghanims_shard']
            if 'aghanims_scepter' in data['hero']:
                gsi_cache['scepter_buff'] = data['hero']['aghanims_scepter']

        # 3. 基于最新的完整缓存池计算总资产
        total_net_worth = gsi_cache['gold']
        has_scepter_item = False 

        for slot_name, item_name in gsi_cache['items'].items():
            if item_name and item_name != "empty":
                api_name = item_name.replace("item_", "")
                if api_name == "ultimate_scepter":
                    has_scepter_item = True
                if api_name in item_prices:
                    total_net_worth += item_prices[api_name]

        if gsi_cache['shard']:
            total_net_worth += 1400
        if gsi_cache['scepter_buff'] and not has_scepter_item:
            total_net_worth += 4200
                
        signals.update_net_worth.emit(total_net_worth)
        return "OK", 200

    app.run(port=PORT, host='127.0.0.1')

# --- 主程序入口 ---
if __name__ == '__main__':
    item_prices = get_item_prices()

    app = QApplication(sys.argv)
    QApplication.setQuitOnLastWindowClosed(False) 

    signals = WorkerSignals()
    overlay = NetWorthOverlay(signals)

    server_thread = threading.Thread(target=run_gsi_server, args=(signals, item_prices), daemon=True)
    server_thread.start()

    monitor_thread = ProcessMonitorThread()
    monitor_thread.status_changed.connect(overlay.handle_dota_status)
    monitor_thread.start()

    sys.exit(app.exec())