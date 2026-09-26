namespace LitematicaViewer.Previewer.Sample;

// 两种查看方式。做成一个枚举而不是几个 bool，是因为「当前是哪个模式」只有一份真相；
// 用两个 bool（比如 WASD 开着、展台开着）就会出现第三种没人定义过的状态，
// 而它一旦出现，表现是「两套手势同时响应」——拖一下又转又走。
internal enum ViewMode
{
    // 自由视角：WASD 走、指针一动就转、右键呼出鼠标、滚轮推拉。
    FreeLook,

    // 展台：位置不动、只缩放，拖动绕圆心公转、松手带惯性、静止后缓慢逆时针自转。
    Showcase
}

// 侧边栏要的那三个口子。做成接口而不是三个委托参数：这一组东西是「当前模式」这一件事的三个侧面，
// 分开传的话，将来多一个侧面就多一个参数，而调用点读起来是一串没说清谁是谁的 lambda。
internal interface IViewModeHost
{
    ViewMode Mode { get; }

    // 展台当前目标的那一行文本。由宿主拼好，侧边栏不去问控制器——
    // 控制器在自由视角下根本不存在，而侧边栏不该知道这件事。
    string ShowcaseTargetCaption { get; }

    // 在两种模式之间来回切。一个入口而不是「切到某个模式」：按钮只有一个，
    // 而两套「切过去」的代码各自都得知道当前是什么，等于把真相抄了两份。
    void ToggleMode();

    void StepTarget(int step);
}
