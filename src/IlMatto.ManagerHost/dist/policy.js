/**
 * Handles only unambiguous local/implementation operations. Questions such as
 * "LaTeX 表格怎么写" remain with the Companion Agent; the model route still
 * handles technical requests that need more context than this small gate.
 */
export function isExplicitCodingRequest(value) {
    const text = value.trim();
    if (!text)
        return false;
    if (/(怎么|如何|是什么|为什么|区别|解释|说明|介绍|教程|示例|语法)/u.test(text))
        return false;
    const operation = /(创建|新建|修改|编辑|读取|写入|写|生成|删除|重命名|保存|移动|复制|编译|构建|运行|执行|安装|修复|实现|重构|调试|提交)/u.test(text);
    if (!operation)
        return false;
    // Natural-language implementation requests do not always say “项目” or
    // “代码”: “用 C++ 写一个图书管理系统” is equally unambiguous. Keep the
    // question/explanation guard above so phrases such as “系统是什么” stay in
    // the companion route.
    const artifact = /(文件|代码|项目|仓库|源码|脚本|终端|命令|测试|编译|构建|系统|程序|应用|软件|工具|服务|函数|模块|组件|网页|接口|bug|问题|git|latex|c\+\+|java|python|typescript|javascript|\.txt\b|\.tex\b|\.latex\b)/iu.test(text);
    return artifact;
}
export function createCodeTask(taskId, originalUserRequest, action) {
    if (action.action !== "delegate_code")
        throw new Error("Only delegate_code actions can create a CodeTask");
    // The manager action's message is deliberately not copied. This is the
    // policy boundary that prevents coordinator-authored technical suggestions
    // from changing the coding specialist's input.
    return { type: "code_task", taskId, userRequest: originalUserRequest };
}
export function sanitizeCoordinatorInput(originalUserRequest) {
    let text = originalUserRequest
        .replace(/```[\s\S]*?```/g, "[local code omitted]")
        .replace(/`[^`\r\n]{24,}`/g, "[local snippet omitted]")
        .replace(/\b(?:[A-Za-z]:\\|\\\\)[^\s\r\n]+/g, "[local path omitted]")
        .replace(/\/(?:Users|home|var|opt|srv|workspace)\/[^\s\r\n]+/gi, "[local path omitted]")
        .replace(/\b(?:sk-[A-Za-z0-9_-]{12,}|AIza[A-Za-z0-9_-]{20,}|Bearer\s+[A-Za-z0-9._~+\/-]{12,})\b/gi, "[credential omitted]");
    const lines = text.split(/\r?\n/).map((line) => {
        if (/^(?:PS\s+[^>]+>|@@\s|\+\+\+\s|---\s|[+\-]\s*[^\s])/i.test(line.trim()))
            return "[local diff or terminal output omitted]";
        if (/^\s*at\s+\S+.*:\d+(?::\d+)?\s*$/i.test(line))
            return "[local stack trace omitted]";
        return line.length > 400 ? `${line.slice(0, 160)}… [long local content omitted]` : line;
    });
    text = lines.join("\n").slice(0, 8_000).trim();
    return text || "The user supplied a local coding artifact. Route this request to the coding specialist.";
}
//# sourceMappingURL=policy.js.map