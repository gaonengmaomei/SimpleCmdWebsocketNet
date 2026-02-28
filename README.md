简易异步指令集 WebSocket 客户端 (SimpleCmdWebsocket)

基于clientWebsocket，支持两种核心交互方式：

主动监听 (CommandMap)：注册指令回调，当收到特定指令消息时自动执行对应的委托，适合处理服务端推送（如心跳、状态更新）。
异步等待 (PendingTcss)：发送指令后直接 await 结果，通过 TaskCompletionSource 机制精准匹配回包，像调用本地方法一样进行网络通讯。
