import { DiagnosticPanel } from "@/components/diagnostics";
import { Button } from "@/components/ui/button";
import { ArrowLeft } from "lucide-react";
import { useNavigate } from "react-router-dom";

const Diagnostics = () => {
  const navigate = useNavigate();

  return (
    <div className="min-h-screen bg-background">
      <div className="max-w-3xl mx-auto px-4 py-8">
        <Button
          variant="ghost"
          size="sm"
          onClick={() => navigate(-1)}
          className="mb-4 text-muted-foreground"
        >
          <ArrowLeft className="h-4 w-4 mr-1" /> Back
        </Button>

        <DiagnosticPanel
          onParamChange={(key, value) => {
            console.log(`[Diagnostics] ${key} → ${value}`);
          }}
        />
      </div>
    </div>
  );
};

export default Diagnostics;
