using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);
int failed = 0;
void Run(string name, Action test)
{
    try { test(); Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); }
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
AgentCircuitData Circuit(UUID id, uint code = 11) => new()
{
    AgentID = id, circuitcode = code, SessionID = UUID.Random(), SecureSessionID = UUID.Random(),
    firstname = "Crossing", lastname = "Regression", child = true, Viewer = "Regression",
    startpos = new Vector3(128,128,30), Appearance = new AvatarAppearance(),
    ServiceURLs = new Dictionary<string, object>(), ChildrenCapSeeds = new Dictionary<ulong,string>()
};

foreach (bool sameCode in new[] { false, true })
{
    Run("stale circuit object cannot remove replacement (same code=" + sameCode + ")", () =>
    {
        var manager = new AgentCircuitManager();
        var old = Circuit(UUID.Random()); var next = Circuit(old.AgentID, sameCode ? 11u : 12u);
        manager.AddNewCircuit(old); manager.AddNewCircuit(next); manager.RemoveCircuit(old);
        Check(ReferenceEquals(manager.GetAgentCircuitData(next.AgentID), next), "replacement UUID mapping deleted");
        Check(manager.AuthenticateSession(next.SessionID, next.AgentID, next.circuitcode).Authorised, "replacement cannot authenticate");
    });
}
Run("replacement invalidates old circuit", () =>
{
    var manager = new AgentCircuitManager(); var old = Circuit(UUID.Random()); var next = Circuit(old.AgentID,12);
    manager.AddNewCircuit(old); manager.AddNewCircuit(next);
    Check(!manager.AuthenticateSession(old.SessionID,old.AgentID,old.circuitcode).Authorised, "old session still authorized");
    manager.RemoveCircuit(old.circuitcode);
    Check(ReferenceEquals(manager.GetAgentCircuitData(next.AgentID),next), "old-code cleanup deleted new UUID mapping");
});
Run("explicit-code overload replaces both indexes", () =>
{
    var manager=new AgentCircuitManager(); var old=Circuit(UUID.Random()); var next=Circuit(old.AgentID);
    manager.AddNewCircuit(11,old); manager.AddNewCircuit(12,next); manager.RemoveCircuit(old);
    Check(manager.GetAgentCircuitData(11u)==null,"old circuit remains");
    Check(ReferenceEquals(manager.GetAgentCircuitData(12u),next),"replacement lost");
});
Run("code collision cleanup cannot delete another avatar", () =>
{
    var manager=new AgentCircuitManager(); var old=Circuit(UUID.Random()); var next=Circuit(UUID.Random());
    manager.AddNewCircuit(old); manager.AddNewCircuit(next); manager.RemoveCircuit(old);
    Check(manager.GetAgentCircuitData(old.AgentID)==null,"displaced UUID remains");
    Check(ReferenceEquals(manager.GetAgentCircuitData(next.AgentID),next),"replacement avatar lost");
    Check(manager.AuthenticateSession(next.SessionID,next.AgentID,next.circuitcode).Authorised,"replacement not authorized");
});
Run("current circuit can still be removed", () =>
{
    var manager = new AgentCircuitManager(); var current = Circuit(UUID.Random()); manager.AddNewCircuit(current);
    manager.RemoveCircuit(current);
    Check(manager.GetAgentCircuitData(current.AgentID) == null && manager.GetAgentCircuitData(current.circuitcode) == null,"current circuit not removed");
});
Run("same-session circuit refresh can still be removed", () =>
{
    var manager = new AgentCircuitManager(); var old = Circuit(UUID.Random()); var refreshed = Circuit(old.AgentID);
    refreshed.SessionID=old.SessionID; refreshed.SecureSessionID=old.SecureSessionID;
    manager.AddNewCircuit(old); manager.AddNewCircuit(refreshed); manager.RemoveCircuit(old);
    Check(manager.GetAgentCircuitData(old.AgentID) == null,"same-session refresh leaked");
});
foreach (string variation in new[] { "session", "circuit", "secure", "same" })
Run("real NewUserConnection with existing child: " + variation, () =>
{
    var old = Circuit(UUID.Random()); var next = Circuit(old.AgentID);
    next.SessionID=old.SessionID; next.SecureSessionID=old.SecureSessionID;
    if (variation=="session") next.SessionID=UUID.Random();
    if (variation=="circuit") next.circuitcode=12;
    if (variation=="secure") next.SecureSessionID=UUID.Random();
    var scene = new SessionScene();
    scene.AuthenticateHandler.AddNewCircuit(old);
    var presence=scene.AddChild(old);
    presence.DoNotCloseAfterTeleport=true;
    Check(scene.NewUserConnection(next,0,null,out string reason,false),"connection refused: "+reason);
    Check(ReferenceEquals(scene.AuthenticateHandler.GetAgentCircuitData(next.AgentID),next),"new circuit missing");
    if(variation=="same")
    {
        Check(scene.CloseCount==0,"valid same-session child was closed");
        Check(ReferenceEquals(scene.GetScenePresence(old.AgentID),presence),"same-session child replaced");
    }
    else
    {
        Check(scene.CloseCount==1,"stale child was reused");
        Check(ReferenceEquals(scene.CircuitAtClose,old),"new circuit registered before old cleanup");
        Check(!scene.CloseWasProtected,"old teleport protection prevented cleanup");
        Check(scene.GetScenePresence(old.AgentID)==null,"stale child remains");
    }
});
Console.WriteLine($"Failures: {failed}");
return failed==0 ? 0 : 1;

public class Stub : DispatchProxy
{
    public Func<MethodInfo,object[],object> Handler;
    protected override object Invoke(MethodInfo method,object[] args)
    {
        if(Handler!=null) return Handler(method,args);
        return Default(method.ReturnType);
    }
    public static object Default(Type type) => type==typeof(void) ? null : type.IsValueType ? Activator.CreateInstance(type) : null;
    public static T Make<T>(Func<MethodInfo,object[],object> handler=null) where T:class
    { var p=Create<T,Stub>(); ((Stub)(object)p).Handler=handler; return p; }
}
public class SessionScene : Scene
{
    public int CloseCount;
    public AgentCircuitData CircuitAtClose;
    public bool CloseWasProtected;
    public SessionScene():base(new RegionInfo {RegionID=UUID.Random(),RegionName="Crossing regression",RegionSizeX=512,RegionSizeY=512})
    {
        m_authenticateHandler=new AgentCircuitManager(); m_config=new IniConfigSource();
        m_UserAccountService=Stub.Make<IUserAccountService>(); LoginsEnabled=true;
        typeof(Scene).GetProperty(nameof(EntityTransferModule)).SetValue(this,Stub.Make<IEntityTransferModule>());
    }
    protected override bool AuthorizeUser(AgentCircuitData agent,bool bypass,out string reason) {reason="";return true;}
    public ScenePresence AddChild(AgentCircuitData circuit)
    {
        IClientAPI client=Stub.Make<IClientAPI>((m,a)=>m.Name switch
        {
            "get_AgentId"=>circuit.AgentID, "get_SessionId"=>circuit.SessionID,
            "get_SecureSessionId"=>circuit.SecureSessionID,"get_CircuitCode"=>circuit.circuitcode,
            "get_FirstName"=>circuit.firstname,"get_LastName"=>circuit.lastname,
            "get_Name"=>"Crossing Regression","get_StartPos"=>circuit.startpos,
            _=>Stub.Default(m.ReturnType)
        });
        return (ScenePresence)typeof(SceneGraph).GetMethod("CreateAndAddChildScenePresence",BindingFlags.Instance|BindingFlags.NonPublic)
            .Invoke(SceneGraph,new object[]{client,circuit.Appearance,PresenceType.User});
    }
    public override bool CloseAgent(UUID id,bool force)
    {
        CloseCount++; CircuitAtClose=AuthenticateHandler.GetAgentCircuitData(id);
        CloseWasProtected=GetScenePresence(id).DoNotCloseAfterTeleport;
        if(CloseWasProtected) return false;
        // Match RemoveClient's real circuit cleanup, with no services, sockets or persistence.
        AuthenticateHandler.RemoveCircuit(CircuitAtClose);
        typeof(SceneGraph).GetMethod("RemoveScenePresence",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(SceneGraph,new object[]{id});
        return true;
    }
}
