<script setup lang="ts">
import { ref, computed } from 'vue'
import { Message } from '@arco-design/web-vue'
import { api } from '../api'

const mode = ref<'app' | 'computer'>('app')

// —— 应用规则 ——
const appName = ref('')
const processNamesText = ref('')
const exePath = ref('')
const appMinutes = ref(120)
const appWeekendMinutes = ref(120)

// —— 运行进程选择器 ——
const pickerVisible = ref(false)
const pickerLoading = ref(false)
const pickerSearch = ref('')
const processes = ref<any[]>([])
const selectedKeys = ref<string[]>([])

async function openPicker() {
  pickerVisible.value = true
  pickerLoading.value = true
  try {
    const data = await api.processes()
    processes.value = data.processes
  } catch (e: any) {
    if (e.name !== 'PinRequiredError') Message.error(e.message)
    pickerVisible.value = false
  } finally {
    pickerLoading.value = false
  }
}

const filteredProcesses = computed(() => {
  const kw = pickerSearch.value.trim().toLowerCase()
  if (!kw) return processes.value
  return processes.value.filter(
    (p: any) =>
      p.name.toLowerCase().includes(kw) ||
      (p.productName || '').toLowerCase().includes(kw) ||
      p.path.toLowerCase().includes(kw),
  )
})

function confirmPicker() {
  const selected = processes.value.filter((p: any) => selectedKeys.value.includes(String(p.pid)))
  if (selected.length === 0) {
    Message.warning('请先选择进程')
    return
  }

  // 同名进程只保留一个（按进程名匹配），路径取第一个
  const byName = new Map<string, any>()
  for (const p of selected) {
    if (!byName.has(p.name)) byName.set(p.name, p)
  }

  const existing = processNamesText.value
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean)
  for (const name of byName.keys()) {
    if (!existing.includes(name)) existing.push(name)
  }
  processNamesText.value = existing.join('\n')
  if (!appName.value) {
    appName.value = selected[0].productName || selected[0].name.replace(/\.exe$/i, '')
  }
  if (!exePath.value && selected.length === 1) {
    exePath.value = ''
  }

  pickerVisible.value = false
  selectedKeys.value = []
  Message.success(`已添加 ${byName.size} 个进程名，可在下方继续调整`)
}

async function submitApp() {
  const names = processNamesText.value
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean)
  if (!appName.value.trim() || names.length === 0) {
    Message.warning('请填写应用名称和至少一个进程名')
    return
  }

  try {
    await api.addAppRule({
      name: appName.value.trim(),
      processNames: names,
      exePath: exePath.value.trim() || undefined,
      minutes: appMinutes.value,
      weekendMinutes: appWeekendMinutes.value,
    })
    Message.success(`已创建应用规则「${appName.value}」，下一个扫描周期生效`)
    appName.value = ''
    processNamesText.value = ''
    exePath.value = ''
  } catch (e: any) {
    if (e.name !== 'PinRequiredError') Message.error(e.message)
  }
}

// —— 整机规则 ——
const computerName = ref('电脑使用')
const computerMinutes = ref(120)
const computerWeekendMinutes = ref(240)

async function submitComputer() {
  try {
    await api.addComputerRule({
      name: computerName.value.trim() || '电脑使用',
      minutes: computerMinutes.value,
      weekendMinutes: computerWeekendMinutes.value,
    })
    Message.success('已创建整机规则，锁屏与空闲时间不计入')
  } catch (e: any) {
    if (e.name !== 'PinRequiredError') Message.error(e.message)
  }
}
</script>

<template>
  <a-card title="添加限制">
    <a-tabs v-model:active-key="mode">
      <a-tab-pane key="app" title="限制指定应用" />
      <a-tab-pane key="computer" title="限制整机使用" />
    </a-tabs>

    <div v-if="mode === 'app'">
      <a-form layout="vertical" style="max-width: 640px">
        <a-form-item label="应用名称">
          <a-input v-model="appName" placeholder="例如：野狐围棋" />
        </a-form-item>
        <a-form-item label="匹配进程（每行一个，多个进程共享同一份额度）">
          <a-textarea v-model="processNamesText" :auto-size="{ minRows: 3, maxRows: 8 }" placeholder="foxwq.exe&#10;foxwqclient.exe" />
          <template #extra>
            <a-button size="small" type="primary" status="success" @click="openPicker">从正在运行的程序选择</a-button>
          </template>
        </a-form-item>
        <a-form-item label="完整路径匹配（可选，填写后仅匹配该路径的程序）">
          <a-input v-model="exePath" placeholder="留空则按进程名匹配" />
        </a-form-item>
        <a-form-item label="每日额度（分钟）">
          <a-space size="large">
            <span>工作日 <a-input-number v-model="appMinutes" :min="1" :max="1440" /></span>
            <span>周末 <a-input-number v-model="appWeekendMinutes" :min="1" :max="1440" /></span>
          </a-space>
        </a-form-item>
        <a-form-item>
          <a-button type="primary" @click="submitApp">创建应用规则</a-button>
        </a-form-item>
      </a-form>
    </div>

    <div v-else>
      <a-form layout="vertical" style="max-width: 640px">
        <a-alert type="info" style="margin-bottom: 16px">
          整机限制统计电脑的实际使用时间：锁屏、空闲和系统睡眠不计入；额度耗尽后自动锁定电脑。
        </a-alert>
        <a-form-item label="规则名称">
          <a-input v-model="computerName" />
        </a-form-item>
        <a-form-item label="每日额度（分钟）">
          <a-space size="large">
            <span>工作日 <a-input-number v-model="computerMinutes" :min="1" :max="1440" /></span>
            <span>周末 <a-input-number v-model="computerWeekendMinutes" :min="1" :max="1440" /></span>
          </a-space>
        </a-form-item>
        <a-form-item>
          <a-button type="primary" @click="submitComputer">创建整机规则</a-button>
        </a-form-item>
      </a-form>
    </div>
  </a-card>

  <a-modal v-model:visible="pickerVisible" title="选择正在运行的程序" width="780px" :mask-closable="false" @ok="confirmPicker">
    <a-space style="width: 100%; margin-bottom: 8px" fill>
      <a-input-search v-model="pickerSearch" placeholder="按进程名 / 产品名 / 路径过滤" allow-clear style="max-width: 360px" />
      <span style="color: var(--color-text-3)">已选 {{ selectedKeys.length }} 个</span>
    </a-space>
    <a-table
      :data="filteredProcesses"
      :loading="pickerLoading"
      :pagination="{ pageSize: 12 }"
      :scroll="{ y: 360 }"
      row-key="pid"
      :row-selection="{ type: 'checkbox', showCheckedAll: true, selectedRowKeys: selectedKeys }"
      @selection-change="(keys: any) => (selectedKeys = keys.map(String))"
    >
      <template #columns>
        <a-table-column title="" :width="44">
          <template #cell="{ record }">
            <img
              v-if="record.path"
              :src="`/api/icon?path=${encodeURIComponent(record.path)}`"
              width="16"
              height="16"
              style="vertical-align: middle"
              @error="(e: any) => (e.target.style.visibility = 'hidden')"
            />
          </template>
        </a-table-column>
        <a-table-column title="进程名" data-index="name" :width="150" />
        <a-table-column title="产品名称" data-index="productName" :width="180" ellipsis tooltip>
          <template #cell="{ record }">{{ record.productName || '—' }}</template>
        </a-table-column>
        <a-table-column title="路径" data-index="path" ellipsis tooltip />
      </template>
    </a-table>
  </a-modal>
</template>
