#include <amxmodx>

public plugin_init()
{
    register_plugin("Engine failure fixture", "1.0.0", "GoldSrcOps");
    register_srvcmd("goldsrcops_engine_failure", "RunFailure");
}

public RunFailure()
{
    // A success-looking marker must never hide a subsequent assertion failure.
    server_print("ENGINE_FAILURE_SMOKE=passed failures=0");
    server_print("ENGINE_ASSERTION_FAILED intentional negative fixture");
    return PLUGIN_HANDLED;
}
