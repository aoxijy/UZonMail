import type { ISystemConfig } from "src/api/pro/systemInfo";
import { getSystemConfig } from "src/api/pro/systemInfo"
import logger from 'loglevel'

export function useSystemConfig () {
  const systemConfig: Ref<ISystemConfig> = ref({
    name: '懿可仕邮件系统',
    loginWelcome: 'Welcome to YiKeShiMail',
    icon: '',
    copyright: '© 2025 YKSMail',
    icpInfo: '桂ICP备19005629号-4',
  })

  onMounted(async () => {
    try {
      const { data } = await getSystemConfig()
      if (data) systemConfig.value = data
    }
    catch {
      logger.debug('[useSystemConfig] Failed to fetch system config')
    }
  })

  return { systemConfig }
}
