import { TaxiChatbot } from "@/components/TaxiChatbot";
import { Link } from "react-router-dom";

const Index = () => {
  return (
    <div className="relative">
      <div className="fixed top-4 right-4 z-50 flex gap-2">
        <Link
          to="/zones"
          className="bg-gradient-to-r from-[#3b82f6] to-[#2563eb] text-white font-extrabold px-5 py-3 rounded-full shadow-lg hover:shadow-xl hover:scale-105 transition-all flex items-center gap-2 text-sm"
        >
          🗺️ Zone Editor
        </Link>
        <Link
          to="/driver"
          className="bg-gradient-to-r from-[#FFD700] to-[#FFC107] text-black font-extrabold px-5 py-3 rounded-full shadow-lg hover:shadow-xl hover:scale-105 transition-all flex items-center gap-2 text-sm"
        >
          🚕 Driver App
        </Link>
        <Link
          to="/address-test"
          className="bg-gradient-to-r from-[#10b981] to-[#059669] text-white font-extrabold px-5 py-3 rounded-full shadow-lg hover:shadow-xl hover:scale-105 transition-all flex items-center gap-2 text-sm"
        >
          🧪 Address Test
        </Link>
      </div>
      <TaxiChatbot />
    </div>
  );
};

export default Index;
