<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { Message } from '@arco-design/web-vue'
import { api, storedPin, storePin } from '../api'

const pinConfigured = ref(false)
const currentPin = ref('')
const newPin = ref('')
const confirmPin = ref('')

async function load() {
  try {
    const s = await api.settings()
    pinConfigured.value = s.pinConfigured
  } catch {
    // ignore
  }
}

async function savePin() {
  if (newPin.value.trim().length < 4) {
    Message.warning('PIN 至少 4 位')
    return
  }

  if (newPin.value !== confirmPin.value) {
    Message.warning('两次输入不一致')
    return
  }

  try {
    // 已设置 PIN 时，接口要求旧 PIN（从会话中取，没有则用输入框）
    const oldPin = pinConfigured.value ? storedPin() || currentPin.value : null
    if (pinConfigured.value && !oldPin) {
      Message.warning('请先输入当前 PIN（或在首页解锁管理员模式）')
      return
    }

    await api.changePin(newPin.value.trim(), oldPin)
    storePin(newPin.value.trim())
    Message.success('PIN 已保存')
    newPin.value = ''
    confirmPin.value = ''
    currentPin.value = ''
    load()
  } catch (e: any) {
    if (e.name !== 'PinRequiredError') Message.error(e.message)
  }
}

onMounted(load)
</script>

<template>
  <a-row :gutter="16">
    <a-col :span="12">
      <a-card title="管理员 PIN">
        <a-alert v-if="!pinConfigured" type="warning" style="margin-bottom: 16px">
          尚未设置 PIN：当前任何能访问本页的人都可以修改规则。建议立即设置。
        </a-alert>
        <a-form layout="vertical">
          <a-form-item v-if="pinConfigured && !storedPin()" label="当前 PIN">
            <a-input-password v-model="currentPin" placeholder="输入当前 PIN" />
          </a-form-item>
          <a-form-item label="新 PIN">
            <a-input-password v-model="newPin" placeholder="至少 4 位" />
          </a-form-item>
          <a-form-item label="确认新 PIN">
            <a-input-password v-model="confirmPin" @keyup.enter="savePin" />
          </a-form-item>
          <a-button type="primary" @click="savePin">{{ pinConfigured ? '修改 PIN' : '设置 PIN' }}</a-button>
        </a-form>
      </a-card>
    </a-col>
    <a-col :span="12">
      <a-card title="关于">
        <p>WinQuota —— Windows 本地防沉迷与时间配额管理工具。</p>
        <ul class="about-list">
          <li>管理界面由后台服务直接托管，仅监听 127.0.0.1，无需浏览器插件。</li>
          <li>规则修改后最多 5 秒（一个扫描周期）生效。</li>
          <li>数据库与日志位于 %ProgramData%\WinQuota\。</li>
          <li>退出本页面不影响限制：真正的限制由 Windows 服务执行。</li>
        </ul>
      </a-card>
    </a-col>
  </a-row>
</template>

<style scoped>
.about-list {
  padding-left: 18px;
  color: var(--color-text-2);
  line-height: 2;
}
</style>
