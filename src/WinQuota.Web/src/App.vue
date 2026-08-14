<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { Message } from '@arco-design/web-vue'
import { api, storedPin, storePin } from './api'
import pkg from '../package.json'
import Home from './pages/Home.vue'
import Rules from './pages/Rules.vue'
import AddApp from './pages/AddApp.vue'
import Settings from './pages/Settings.vue'

const page = ref('home')
const pageComponents: Record<string, any> = { home: Home, rules: Rules, add: AddApp, settings: Settings }
const pageLabels: Record<string, string> = { home: '今日状态', rules: '限制规则', add: '添加应用', settings: '设置' }

const pinModalVisible = ref(false)
const pinInput = ref('')

function onPinRequired() {
  if (pinModalVisible.value) return
  pinInput.value = ''
  pinModalVisible.value = true
}

async function submitPin(done: (ok: boolean) => void) {
  try {
    const { ok } = await api.verifyPin(pinInput.value)
    if (ok) {
      storePin(pinInput.value)
      Message.success('管理员模式已解锁')
      window.dispatchEvent(new CustomEvent('winquota:pin-ok'))
    } else {
      Message.error('PIN 错误')
    }
    done(ok)
  } catch {
    done(false)
  }
}

function clearPin() {
  storePin(null)
  Message.info('已退出管理员模式（仅本次会话）')
}

onMounted(() => window.addEventListener('winquota:pin-required', onPinRequired))
onUnmounted(() => window.removeEventListener('winquota:pin-required', onPinRequired))
</script>

<template>
  <a-layout style="min-height: 100vh">
    <a-layout-sider :width="180" class="sider">
      <div class="brand">🛡️ WinQuota<br /><span class="brand-sub">防沉迷管理</span></div>
      <nav class="nav">
        <button v-for="(label, key) in pageLabels" :key="key" :class="{ active: page === key }" @click="page = key">
          {{ label }}
        </button>
      </nav>
      <div class="sider-footer">
        <button v-if="storedPin()" class="pin-btn" @click="clearPin">退出管理员模式</button>
        <div class="ver">本地运行 · v{{ pkg.version }}</div>
      </div>
    </a-layout-sider>
    <a-layout>
      <a-layout-content style="padding: 20px 28px">
        <component :is="pageComponents[page]" />
      </a-layout-content>
    </a-layout>
  </a-layout>

  <a-modal v-model:visible="pinModalVisible" title="管理员 PIN" :mask-closable="false" @before-ok="(done: any) => submitPin(done)">
    <a-input-password v-model="pinInput" placeholder="请输入管理员 PIN" @keyup.enter="submitPin(() => {})" />
  </a-modal>
</template>

<style scoped>
.sider {
  display: flex;
  flex-direction: column;
  padding: 16px 10px;
  gap: 12px;
  background: var(--color-bg-2);
  border-right: 1px solid var(--color-border);
}
.brand {
  font-size: 18px;
  font-weight: 700;
  line-height: 1.35;
  padding: 4px 8px;
}
.brand-sub {
  font-size: 13px;
  font-weight: 500;
  color: var(--color-text-2);
}
.nav {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.nav button {
  border: none;
  background: transparent;
  padding: 10px 14px;
  font-size: 14px;
  text-align: left;
  cursor: pointer;
  border-radius: 6px;
  color: var(--color-text-1);
}
.nav button:hover {
  background: var(--color-fill-2);
}
.nav button.active {
  background: rgb(var(--primary-1));
  font-weight: 600;
  color: rgb(var(--primary-6));
}
.sider-footer {
  margin-top: auto;
  padding: 8px;
}
.pin-btn {
  border: none;
  background: transparent;
  color: var(--color-text-3);
  font-size: 12px;
  cursor: pointer;
  padding: 6px 4px;
}
.pin-btn:hover {
  color: var(--color-text-1);
}
.ver {
  font-size: 11px;
  color: var(--color-text-4);
  margin-top: 6px;
}
</style>
