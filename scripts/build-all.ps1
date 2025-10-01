# 设置代码页为 UTF-8
chcp 65001

# 编译 windows desktop
Write-Host "1. 开始编译 windows desktop ..." -ForegroundColor Yellow
$winDesktop = & ./build-core.ps1 -platform win -desktop $true
Write-Host "-------------------------windows desktop 编译完成(1/4)-------------------------" -ForegroundColor Green

# 编译 windows server
Write-Host "2. 开始编译 windows server ..." -ForegroundColor Yellow
$winServer = . ./build-core.ps1 -platform win -rebuildFrontend $false
Write-Host "-------------------------windows server 编译完成(2/4)-------------------------" -ForegroundColor Green
Write-Host ""

# 编译 linux server
Write-Host "3. 开始编译 linux server ..." -ForegroundColor Yellow
$linuxServer = . ./build-core.ps1 -platform linux -rebuildFrontend $false -docker $true
Write-Host "-------------------------linux server 编译完成(3/4)-------------------------" -ForegroundColor Green
Write-Host ""

# 上传到网络
# 若存在 od 命令，则使用 od 命令上传
function New-Oss {  
  $paths = @($winDesktop[-1], $winServer[-1], $linuxServer[-1])
  Write-Host "4. 开始上传到网络 ..." -ForegroundColor Yellow
  $results = @()
  foreach ($path in $paths) {
    # 判断文件存在
    if (-not (Test-Path $path)) {
      Write-Host "$path 不存在！" -ForegroundColor Red
      continue
    }

    # 开始上传 - 添加平台兼容性检查
    try {
        if ($IsWindows) {
            # Windows 环境使用原来的命令
            $result = od minio soft -p $path
        } else {
            # Linux 环境使用兼容的命令格式
            # 尝试不同的参数组合
            $result = od minio soft --put $path
        }
        
        # 安全地获取最后一行结果
        if ($result -and $result.Length -gt 0) {
            $results += $result[-1]
            Write-Host "成功上传: $path" -ForegroundColor Green
        } else {
            Write-Host "上传命令执行但无输出: $path" -ForegroundColor Yellow
            $results += "上传完成但无返回信息"
        }
    }
    catch {
        Write-Host "上传失败: $path - 错误: $($_.Exception.Message)" -ForegroundColor Red
        $results += "上传失败"
    }
  }

  # 输出地址
  Write-Host "-------------------------上传到网络完成(4/4)-------------------------" -ForegroundColor Green
  Write-Host "上传结果：" -ForegroundColor Green
  $results
}

# 检查 od 命令是否存在且可用
if (Get-Command od -ErrorAction SilentlyContinue) {
  try {
      # 测试 od 命令是否工作
      od --help *>$null
      New-Oss
  }
  catch {
      Write-Host "od 命令测试失败，跳过上传步骤: $($_.Exception.Message)" -ForegroundColor Yellow
  }
} else {
  Write-Host "未找到 od 命令，跳过上传步骤" -ForegroundColor Yellow
}
