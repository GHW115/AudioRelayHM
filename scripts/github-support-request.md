# GitHub Support 工单 — 清除历史中的签名密钥

提交路径：https://support.github.com/request
类别选择：**Security** → 按表单填写（或直接选 "Account/Repository issues" 后描述即可）

## 仓库
`GHW115/AudioRelayHM`

## 泄露内容
HarmonyOS 应用签名证书密码（build-profile.json5 中的 storePassword / keyPassword 明文）
以及用户主目录证书路径（含用户名）。

## 需要清除的旧提交 SHA（force push 前已推送过）
```
9be246523bcd199cccfc9d4bc2fa4f204921771a   # 初次提交（含明文密钥）
0cb3b60110479e0ae6610e00babdaa083f6c9497   # feat: 0ms缓冲（含明文密钥）
47f6be756f1825d6d947abcccc01fd50138d21d2   # 重写前的 master HEAD
90a6e019e193680c3fb122e242f66a687cbbc72f   # 重写前 v1.1.0 tag
3c269154916843b8776f597aba54e2f3b33ff9d8   # 重写前 v1.2.0 tag（annotated tag 对象）
```

## 英文模板（复制即用）

```
Subject: Request to purge cached commits containing exposed secrets

Hello GitHub Support,

I accidentally committed sensitive signing credentials (HarmonyOS
app-signing certificate passwords) to the public repository
GHW115/AudioRelayHM. I have already force-pushed a rewritten history
that removes the secrets, but the old commits remain reachable through
GitHub's caches.

Please purge the following commit objects from GitHub's systems:

- 9be246523bcd199cccfc9d4bc2fa4f204921771a
- 0cb3b60110479e0ae6610e00babdaa083f6c9497
- 47f6be756f1825d6d947abcccc01fd50138d21d2
- 90a6e019e193680c3fb122e242f66a687cbbc72f
- 3c269154916843b8776f597aba54e2f3b33ff9d8

The secrets are contained in the file hmos/build-profile.json5 of
those commits (storePassword / keyPassword fields, and certificate
paths including my Windows username).

The repository has no forks, so purging these objects should fully
remove the exposure. Let me know if you need anything else from me.

Thank you.
```

## 提交后
- GitHub 通常 1~3 个工作日处理
- 处理完成后可用 `gh api repos/GHW115/AudioRelayHM/commits/9be2465...` 验证（返回 404 即已清除）
