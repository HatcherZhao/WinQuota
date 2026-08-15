<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { Message } from '@arco-design/web-vue'
import { api, type RuleDetail } from '../api'
import { fmtMinutes } from '../format'

const rules = ref<RuleDetail[]>([])
const loading = ref(false)

const editVisible = ref(false)
const editRule = ref<RuleDetail | null>(null)
const editMinutes = ref(0)
const editWeekend = ref(0)

const detailVisible = ref(false)
const detailRule = ref<RuleDetail | null>(null)
const editName = ref('')
const editProcesses = ref('')

async function load() {
  loading.value = true
  try {
    const data = await api.rules()
    rules.value = data.rules
  } catch (e: any) {
    if (e.name !== 'PinRequiredError') Message.error(e.message)
  } finally {
    loading.value = false
  }
}

const rows = computed(() =>
  rules.value.map((r) => ({
    ...r,
    processList: r.apps.map((a) => a.processName).join('、'),
    weekday: fmtMinutes(r.weekdayQuotaSeconds[0]),
    weekend: fmtMinutes(r.weekdayQuotaSeconds[5]),
  })),
)

async function toggle(row: any, enabled: boolean) {
  try {
    await api.enableRule(row.id, enabled)
    row.enabled = enabled
    Message.success(enabled ? '已启用' : '已禁用')
  } catch (e: any) {
    if (e.name !== 'PinRequiredError') Message.error(e.message)
  }
}

async function addBonus(row: any, minutes: number) {
  try {
    await api.bonus(row.id, minutes)
    Message.success(`已为「${row.name}」增加 ${minutes} 分钟（仅今天）`)
  } catch (e: any) {
    if (e.name !== 'PinRequiredError') Message.error(e.message)
  }
}

function openEdit(row: any) {
  editRule.value = row
  editMinutes.value = Math.round(row.weekdayQuotaSeconds[0] / 60)
  editWeekend.value = Math.round(row.weekdayQuotaSeconds[5] / 60)
  editVisible.value = true
}

async function saveEdit() {
  if (!editRule.value) return
  try {
    await api.updateRule({
      id: editRule.value.id,
      minutes: editMinutes.value,
      weekendMinutes: editWeekend.value,
    })
    Message.success('额度已更新，下一个扫描周期生效')
    editVisible.value = false
    load()
  } catch (e: any) {
    if (e.name !== 'PinRequiredError') Message.error(e.message)
  }
}

function openDetail(row: any) {
  detailRule.value = row
  editName.value = row.name
  editProcesses.value = row.apps.map((a: any) => a.processName).join('\n')
  detailVisible.value = true
}

async function saveDetail() {
  if (!detailRule.value) return
  const processes = editProcesses.value
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean)
  try {
    const body: any = { id: detailRule.value.id, name: editName.value.trim() }
    if (detailRule.value.type === 'application') {
      if (processes.length === 0) {
        Message.warning('应用规则至少需要一个进程名')
        return
      }
      body.processNames = processes
    }
    await api.editRule(body)
    Message.success('规则已更新（额度与用量历史保留），下一个扫描周期生效')
    detailVisible.value = false
    load()
  } catch (e: any) {
    if (e.name !== 'PinRequiredError') Message.error(e.message)
  }
}

async function remove(row: any) {
  try {
    await api.deleteRule(row.id)
    Message.success(`已删除「${row.name}」`)
    load()
  } catch (e: any) {
    if (e.name !== 'PinRequiredError') Message.error(e.message)
  }
}

onMounted(load)
</script>

<template>
  <a-card title="限制规则">
    <a-table :data="rows" :loading="loading" :pagination="false" row-key="id">
      <template #columns>
        <a-table-column title="名称" data-index="name">
          <template #cell="{ record }">
            <a-space>
              <span>{{ record.name }}</span>
              <a-tag v-if="record.type === 'computer'" color="arcoblue" size="small">整机</a-tag>
            </a-space>
          </template>
        </a-table-column>
        <a-table-column title="匹配进程" data-index="processList" ellipsis tooltip>
          <template #cell="{ record }">
            <span v-if="record.type === 'computer'">—</span>
            <span v-else>{{ record.processList }}</span>
          </template>
        </a-table-column>
        <a-table-column title="工作日额度" data-index="weekday" :width="110" />
        <a-table-column title="周末额度" data-index="weekend" :width="110" />
        <a-table-column title="启用" :width="80">
          <template #cell="{ record }">
            <a-switch :model-value="record.enabled" @change="(v: any) => toggle(record, !!v)" />
          </template>
        </a-table-column>
        <a-table-column title="操作" :width="330">
          <template #cell="{ record }">
            <a-space>
              <a-dropdown @select="(min: any) => addBonus(record, Number(min))">
                <a-button size="small" type="outline">+奖励</a-button>
                <template #content>
                  <a-doption :value="15">+15 分钟（仅今天）</a-doption>
                  <a-doption :value="30">+30 分钟（仅今天）</a-doption>
                  <a-doption :value="60">+60 分钟（仅今天）</a-doption>
                </template>
              </a-dropdown>
              <a-button size="small" @click="openEdit(record)">改额度</a-button>
              <a-button size="small" @click="openDetail(record)">编辑</a-button>
              <a-popconfirm content="确定删除这条规则？" type="warning" @ok="remove(record)">
                <a-button size="small" status="danger">删除</a-button>
              </a-popconfirm>
            </a-space>
          </template>
        </a-table-column>
      </template>
    </a-table>
    <a-empty v-if="!loading && rows.length === 0" description="暂无规则" />
  </a-card>

  <a-modal v-model:visible="detailVisible" :title="`编辑规则：${detailRule?.name || ''}`" @ok="saveDetail">
    <a-form layout="vertical">
      <a-form-item label="规则名称">
        <a-input v-model="editName" />
      </a-form-item>
      <a-form-item v-if="detailRule?.type === 'application'" label="匹配进程（每行一个，保存后原位替换；额度与今日已用时间保留）">
        <a-textarea v-model="editProcesses" :auto-size="{ minRows: 3, maxRows: 8 }" placeholder="foxwq.exe&#10;foxwqclient.exe" />
      </a-form-item>
    </a-form>
  </a-modal>

  <a-modal v-model:visible="editVisible" :title="`修改额度：${editRule?.name || ''}`" @ok="saveEdit">
    <a-form :model="null" layout="vertical">
      <a-form-item label="工作日额度（分钟）">
        <a-input-number v-model="editMinutes" :min="1" :max="1440" />
      </a-form-item>
      <a-form-item label="周末额度（分钟）">
        <a-input-number v-model="editWeekend" :min="1" :max="1440" />
      </a-form-item>
    </a-form>
  </a-modal>
</template>
