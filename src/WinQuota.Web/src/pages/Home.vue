<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { Message } from '@arco-design/web-vue'
import { api, type StatusPayload, type RuleStatus } from '../api'
import { fmtDuration, computerStateText } from '../format'

const extensionsLeft = (r: RuleStatus) => Math.max(0, r.extensionsMax - r.extensionsUsed)

async function doExtend(r: RuleStatus) {
  try {
    await api.extend(r.id)
    Message.success(`已延期 ${r.extensionMinutes} 分钟`)
    refresh()
  } catch (e: any) {
    Message.error(e.message || '延期失败')
    refresh()
  }
}

const status = ref<StatusPayload | null>(null)
const statDays = ref(7)
const usageData = ref<any>(null)

let timer: number | undefined

async function refresh() {
  try {
    status.value = await api.status()
  } catch {
    // 服务重启中，下一轮再试
  }
}

async function refreshUsage() {
  try {
    usageData.value = await api.usage(statDays.value)
  } catch {
    // ignore
  }
}

function onPinOk() {
  refreshUsage()
}

const stateText = computed(() => {
  if (!status.value) return ''
  return computerStateText[status.value.computerState] || status.value.computerState
})

// 进度条显示剩余比例（Arco percent 取值 0~1）：满条 = 满额度，越用越短
const percent = (r: RuleStatus) => {
  const total = r.quotaSeconds + r.bonusSeconds
  return total <= 0 ? 0 : Math.max(0, Math.min(1, r.remainingSeconds / total))
}

const chart = computed(() => {
  if (!usageData.value?.rules) return []
  return usageData.value.rules.map((r: any) => {
    const max = Math.max(...r.days.map((d: any) => d.usedSeconds), 60)
    return {
      name: `${r.name}${r.type === 'computer' ? '（整机）' : ''}`,
      bars: r.days.map((d: any) => ({
        date: d.date.slice(5),
        used: d.usedSeconds,
        heightPct: Math.max(2, Math.round((d.usedSeconds / max) * 100)),
      })),
    }
  })
})

onMounted(() => {
  refresh()
  refreshUsage()
  timer = window.setInterval(refresh, 5000)
  window.addEventListener('winquota:pin-ok', onPinOk)
})
onUnmounted(() => {
  if (timer) window.clearInterval(timer)
  window.removeEventListener('winquota:pin-ok', onPinOk)
})
</script>

<template>
  <a-alert v-if="status" class="state-bar" type="info">
    {{ status.date }} · 电脑状态：<b>{{ stateText }}</b> · 数据更新于
    {{ new Date(status.liveUpdateUtc).toLocaleTimeString() }}
  </a-alert>

  <a-spin :loading="!status" style="width: 100%">
    <a-row :gutter="16">
      <a-col v-for="r in status?.rules || []" :key="r.id" :span="12" style="margin-bottom: 16px">
        <a-card>
          <template #title>
            <a-space>
              <img
                v-if="r.iconPath"
                :src="`/api/icon?path=${encodeURIComponent(r.iconPath)}`"
                width="20"
                height="20"
                style="vertical-align: middle"
                @error="(e: any) => (e.target.style.visibility = 'hidden')"
              />
              <span>{{ r.name }}</span>
              <a-tag v-if="r.type === 'computer'" color="arcoblue">整机</a-tag>
              <a-tag v-else color="green">应用</a-tag>
              <a-tag v-if="!r.enabled" color="gray">已禁用</a-tag>
              <a-tag v-if="r.running" color="orangered">运行中</a-tag>
            </a-space>
          </template>
          <div class="usage-line">
            <span>已用 {{ fmtDuration(r.usedSeconds) }} / {{ fmtDuration(r.quotaSeconds + r.bonusSeconds) }}</span>
            <a-tag v-if="r.bonusSeconds > 0" color="gold">含奖励 {{ fmtDuration(r.bonusSeconds) }}</a-tag>
            <span class="remaining">剩余 {{ fmtDuration(r.remainingSeconds) }}</span>
          </div>
          <!-- Arco 2.58 的 a-progress 没有 format prop，百分比文本必须用 #text 插槽自定义 -->
          <a-progress
            :percent="percent(r)"
            :status="r.remainingSeconds === 0 ? 'warning' : undefined"
          >
            <template #text="{ percent: p }">{{ (p * 100).toFixed(2) }}%</template>
          </a-progress>
          <div v-if="r.remainingSeconds === 0 && extensionsLeft(r) > 0" class="extend-line">
            <a-button type="outline" status="warning" size="small" @click="doExtend(r)">
              延期 {{ r.extensionMinutes }} 分钟（今日还剩 {{ extensionsLeft(r) }} 次）
            </a-button>
          </div>
          <div v-if="r.processes.length" class="procs">
            进程：<a-tag v-for="p in r.processes" :key="p.pid" color="gray">{{ p.name }} ({{ p.pid }})</a-tag>
          </div>
        </a-card>
      </a-col>
    </a-row>
    <a-empty v-if="status && status.rules.length === 0" description="还没有限制规则，去“添加应用”创建一条吧" />
  </a-spin>

  <a-card title="使用统计" style="margin-top: 8px">
    <template #extra>
      <a-radio-group v-model="statDays" type="button" size="small" @change="refreshUsage">
        <a-radio :value="7">最近 7 天</a-radio>
        <a-radio :value="30">最近 30 天</a-radio>
      </a-radio-group>
    </template>
    <div v-for="c in chart" :key="c.name" class="chart-block">
      <div class="chart-name">{{ c.name }}</div>
      <div class="chart-bars">
        <div v-for="b in c.bars" :key="b.date" class="bar-col" :title="`${b.date}：${fmtDuration(b.used)}`">
          <div class="bar" :style="{ height: b.heightPct + '%' }"></div>
          <div class="bar-label">{{ b.date }}</div>
        </div>
      </div>
    </div>
    <a-empty v-if="chart.length === 0" description="暂无统计数据" />
  </a-card>
</template>

<style scoped>
.state-bar {
  margin-bottom: 16px;
}
.usage-line {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-bottom: 8px;
}
.remaining {
  margin-left: auto;
  font-weight: 600;
}
.procs {
  margin-top: 8px;
  color: var(--color-text-2);
}
.extend-line {
  margin-top: 10px;
}
.chart-block {
  margin-bottom: 20px;
}
.chart-name {
  font-weight: 600;
  margin-bottom: 8px;
}
.chart-bars {
  display: flex;
  align-items: flex-end;
  gap: 4px;
  height: 120px;
}
.bar-col {
  flex: 1;
  display: flex;
  flex-direction: column;
  justify-content: flex-end;
  height: 100%;
  min-width: 0;
}
.bar {
  background: var(--color-primary-light-2);
  border-radius: 2px 2px 0 0;
  width: 100%;
}
.bar-label {
  font-size: 10px;
  color: var(--color-text-3);
  text-align: center;
  transform: scale(0.85);
  white-space: nowrap;
}
</style>
